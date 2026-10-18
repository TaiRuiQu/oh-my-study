//! Typed Tauri boundary. No generic filesystem, shell, database or HTTP commands.
use crate::{
    ai::providers::transport::{Credentials, Transport},
    credentials,
    domain::grading::{GradingInput, GradingResult, Question, SavedQuestion},
    domain::{AppSettings, Document, ImportMode, ModelConfig, PageEdit, Provider},
    execution::{Capability, CapabilityState, ExecutionMode, ExecutionPolicy, ExecutorKind},
    jobs::{DiagnosticKind, Job, JobConfig, JobEvent, JobManager, PermissionChoice},
    storage::{ImportSource, Storage, MAX_ASSET_BYTES, MAX_INPUT_PAGES},
};
use serde::Serialize;
use std::{
    collections::{BTreeMap, HashSet},
    fs::{self, File},
    io::Read,
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, AtomicU64, Ordering},
        Arc, Mutex,
    },
    time::Duration,
};
use tauri::{ipc::Channel, Manager, State};
use tauri_plugin_dialog::DialogExt;
use tokio_util::sync::CancellationToken;

struct DesktopState {
    storage: Storage,
    jobs: JobManager,
    capability: Capability,
    data_directory: PathBuf,
    selected_paths: Mutex<HashSet<PathBuf>>,
    subscription: AtomicU64,
    provider_mutation: tokio::sync::Mutex<()>,
    operations: tokio::sync::RwLock<()>,
    shutting_down: CancellationToken,
    shutdown_complete: AtomicBool,
}
type AppState<'a> = State<'a, Arc<DesktopState>>;

#[derive(Serialize)]
struct Environment {
    policy: ExecutionPolicy,
    capability: Capability,
    data_directory: String,
}
#[derive(Serialize)]
struct Bootstrap {
    documents: Vec<Document>,
    providers: Vec<Provider>,
    settings: AppSettings,
    environment: Environment,
    jobs: Vec<Job>,
}
#[derive(Serialize)]
struct AssetReply {
    mime: String,
    bytes: Vec<u8>,
}

fn mode(settings: &AppSettings) -> Result<ExecutionMode, String> {
    serde_json::from_value(serde_json::Value::String(settings.execution_mode.clone()))
        .map_err(|_| "无效的执行权限模式".into())
}
fn environment(state: &DesktopState, settings: &AppSettings) -> Result<Environment, String> {
    let mut policy = ExecutionPolicy::freeze(
        Some(mode(settings)?),
        &state.capability,
        settings.sandbox_networking,
    );
    if policy.executor_kind == ExecutorKind::Host && policy.warning.is_none() {
        policy.warning = Some(
            "本机执行／未隔离：代码与命令使用当前 Windows 账户权限，可访问该账户的文件和网络。"
                .into(),
        );
    }
    Ok(Environment {
        policy,
        capability: state.capability.clone(),
        data_directory: state.data_directory.to_string_lossy().into_owned(),
    })
}
async fn operation(state: &DesktopState) -> Result<tokio::sync::RwLockReadGuard<'_, ()>, String> {
    let guard = state.operations.read().await;
    if state.shutting_down.is_cancelled() {
        return Err("应用正在退出，不能开始新操作".into());
    }
    Ok(guard)
}
async fn provider_list(storage: Storage) -> Result<Vec<Provider>, String> {
    let mut providers = storage.list_providers().await.map_err(|e| e.to_string())?;
    tauri::async_runtime::spawn_blocking(move || {
        for provider in &mut providers {
            provider.has_key = provider
                .key_ref
                .as_deref()
                .and_then(|reference| credentials::read(reference).ok().flatten())
                .is_some();
        }
        providers
    })
    .await
    .map_err(|_| "凭据检查线程中断".to_string())
}

#[tauri::command]
async fn bootstrap(state: AppState<'_>) -> Result<Bootstrap, String> {
    let settings = state
        .storage
        .get_settings()
        .await
        .map_err(|e| e.to_string())?;
    let documents = state
        .storage
        .list_documents()
        .await
        .map_err(|e| e.to_string())?;
    let providers = provider_list(state.storage.clone()).await?;
    let shared = state.inner().clone();
    let jobs = tauri::async_runtime::spawn_blocking(move || shared.jobs.list())
        .await
        .map_err(|_| "任务查询中断")??;
    Ok(Bootstrap {
        environment: environment(&state, &settings)?,
        settings,
        documents,
        providers,
        jobs,
    })
}

#[tauri::command]
async fn list_documents(state: AppState<'_>) -> Result<Vec<Document>, String> {
    state
        .storage
        .list_documents()
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn select_import_files(
    app: tauri::AppHandle,
    state: AppState<'_>,
) -> Result<Vec<String>, String> {
    if state.shutting_down.is_cancelled() {
        return Err("应用正在退出".into());
    }
    let paths = tauri::async_runtime::spawn_blocking(move || {
        app.dialog()
            .file()
            .add_filter("图片或 PDF", &["jpg", "jpeg", "png", "webp", "pdf"])
            .blocking_pick_files()
            .unwrap_or_default()
            .into_iter()
            .map(|path| path.into_path().map_err(|_| "请选择本地文件".to_string()))
            .collect::<Result<Vec<_>, _>>()
    })
    .await
    .map_err(|_| "文件选择中断")??;
    let mut selected = state
        .selected_paths
        .lock()
        .map_err(|_| "文件选择状态无效")?;
    let mut result = Vec::new();
    for path in paths {
        let path = checked_input(&path)?;
        result.push(path.to_string_lossy().into_owned());
        selected.insert(path);
    }
    Ok(result)
}

fn checked_input(path: &Path) -> Result<PathBuf, String> {
    if !path.is_absolute() {
        return Err("只支持本地绝对文件路径".into());
    }
    crate::execution::bridge::reject_reparse_components(path)?;
    let path = path
        .canonicalize()
        .map_err(|_| "无法读取选中的文件".to_string())?;
    if !path.is_file() {
        return Err("所选路径不是文件".into());
    }
    Ok(path)
}

#[tauri::command(rename_all = "snake_case")]
async fn import_files(
    state: AppState<'_>,
    paths: Vec<String>,
    name: String,
    mode: ImportMode,
    reference_paths: Vec<String>,
) -> Result<Document, String> {
    let _operation = operation(&state).await?;
    let count = paths
        .len()
        .checked_add(reference_paths.len())
        .ok_or("文件数量超限")?;
    if paths.is_empty() || count > MAX_INPUT_PAGES {
        return Err("请选择材料，图片与 PDF 解析后总计最多 12 页".into());
    }
    let shared = state.inner().clone();
    let sources = tauri::async_runtime::spawn_blocking(move || {
        read_selected(
            &shared,
            paths
                .into_iter()
                .map(|p| (p, "material"))
                .chain(reference_paths.into_iter().map(|p| (p, "reference"))),
        )
    })
    .await
    .map_err(|_| "材料读取线程中断")??;
    state
        .storage
        .import_document(name, mode, sources)
        .await
        .map_err(|e| e.to_string())
}

fn read_selected(
    shared: &DesktopState,
    paths: impl Iterator<Item = (String, &'static str)>,
) -> Result<Vec<ImportSource>, String> {
    let mut sources = Vec::new();
    let mut total = 0usize;
    for (path, role) in paths {
        let path = checked_input(Path::new(&path))?;
        if !shared
            .selected_paths
            .lock()
            .map_err(|_| "文件选择状态无效")?
            .contains(&path)
        {
            return Err("请通过系统文件选择器或拖入文件授权本次材料".into());
        }
        let file = File::open(&path).map_err(|_| "无法打开材料文件".to_string())?;
        let mut bytes = Vec::new();
        file.take(MAX_ASSET_BYTES as u64 + 1)
            .read_to_end(&mut bytes)
            .map_err(|_| "材料读取失败")?;
        if bytes.len() > MAX_ASSET_BYTES {
            return Err("单文件不能超过 64 MiB".into());
        }
        total = total.checked_add(bytes.len()).ok_or("材料大小超限")?;
        if total > 256 * 1024 * 1024 {
            return Err("本批材料不能超过 256 MiB".into());
        }
        sources.push(ImportSource {
            name: path
                .file_name()
                .unwrap_or_default()
                .to_string_lossy()
                .into_owned(),
            bytes,
            role: role.into(),
        });
    }
    Ok(sources)
}

#[tauri::command(rename_all = "snake_case")]
async fn append_reference_files(
    state: AppState<'_>,
    document_id: String,
    paths: Vec<String>,
) -> Result<Document, String> {
    let _operation = operation(&state).await?;
    if paths.is_empty() || paths.len() > MAX_INPUT_PAGES {
        return Err("请选择 1–12 个参考答案输入".into());
    }
    let shared = state.inner().clone();
    let sources = tauri::async_runtime::spawn_blocking(move || {
        read_selected(&shared, paths.into_iter().map(|path| (path, "reference")))
    })
    .await
    .map_err(|_| "参考答案读取中断")??;
    state
        .storage
        .append_document_sources(document_id, sources)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command(rename_all = "snake_case")]
async fn update_pages(
    state: AppState<'_>,
    document_id: String,
    pages: Vec<PageEdit>,
) -> Result<Document, String> {
    let _operation = operation(&state).await?;
    state
        .storage
        .update_pages(document_id, pages)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command(rename_all = "snake_case")]
async fn read_asset(
    state: AppState<'_>,
    asset_id: String,
    thumbnail: bool,
) -> Result<AssetReply, String> {
    let asset = state
        .storage
        .read_asset(asset_id, thumbnail)
        .await
        .map_err(|e| e.to_string())?;
    Ok(AssetReply {
        mime: asset.mime,
        bytes: asset.bytes,
    })
}

#[tauri::command]
async fn save_provider(
    state: AppState<'_>,
    mut provider: Provider,
    key: Option<String>,
) -> Result<Provider, String> {
    let _operation = operation(&state).await?;
    let _guard = state.provider_mutation.lock().await;
    if provider.id.is_empty() {
        provider.id = uuid::Uuid::new_v4().to_string();
    }
    uuid::Uuid::parse_str(&provider.id).map_err(|_| "无效的供应商 ID")?;
    // Reject credential-bearing URL/header tricks before creating a vault entry.
    Transport::new(provider.protocol, &provider.base_url, &BTreeMap::new())
        .map_err(|e| e.to_string())?;
    let old_reference = state
        .storage
        .list_providers()
        .await
        .map_err(|e| e.to_string())?
        .into_iter()
        .find(|p| p.id == provider.id)
        .and_then(|p| p.key_ref);
    provider.key_ref = old_reference.clone();
    let new_reference = if let Some(key) = key.filter(|value| !value.is_empty()) {
        let reference = credentials::new_reference();
        let next = reference.clone();
        tauri::async_runtime::spawn_blocking(move || credentials::save(&next, &key))
            .await
            .map_err(|_| "密钥保存线程中断")??;
        provider.key_ref = Some(reference.clone());
        Some(reference)
    } else {
        None
    };
    let saved = state.storage.upsert_provider(provider).await;
    match saved {
        Ok(mut provider) => {
            let reference = provider.key_ref.clone();
            provider.has_key = tauri::async_runtime::spawn_blocking(move || {
                if new_reference.is_some() {
                    if let Some(old) = old_reference {
                        let _ = credentials::delete(&old);
                    }
                }
                reference
                    .and_then(|value| credentials::read(&value).ok().flatten())
                    .is_some()
            })
            .await
            .map_err(|_| "密钥状态检查中断")?;
            Ok(provider)
        }
        Err(error) => {
            if let Some(reference) = new_reference {
                let _ =
                    tauri::async_runtime::spawn_blocking(move || credentials::delete(&reference))
                        .await;
            }
            Err(error.to_string())
        }
    }
}

#[tauri::command(rename_all = "snake_case")]
async fn refresh_models(
    state: AppState<'_>,
    provider_id: String,
) -> Result<Vec<ModelConfig>, String> {
    let _operation = operation(&state).await?;
    let _guard = state.provider_mutation.lock().await;
    let mut provider = state
        .storage
        .list_providers()
        .await
        .map_err(|e| e.to_string())?
        .into_iter()
        .find(|p| p.id == provider_id)
        .ok_or("找不到供应商")?;
    let reference = provider.key_ref.clone().ok_or("请先保存供应商密钥")?;
    let secret = tauri::async_runtime::spawn_blocking(move || credentials::read(&reference))
        .await
        .map_err(|_| "密钥读取线程中断")??
        .ok_or("密钥不存在，请重新配置")?;
    let credential = Credentials::new(secret.expose().to_owned()).map_err(|e| e.to_string())?;
    let transport = Transport::new(provider.protocol, &provider.base_url, &BTreeMap::new())
        .map_err(|e| e.to_string())?;
    let mut cursor: Option<String> = None;
    let mut seen_cursors = HashSet::new();
    let mut models = provider.models.clone();
    for page_index in 0..20 {
        let page = transport
            .list_models(&credential, cursor.as_deref(), &state.shutting_down)
            .await
            .map_err(|e| e.to_string())?;
        for model in page.models {
            if let Some(existing) = models.iter_mut().find(|m| m.id == model.id) {
                if model.capabilities.source.is_some() {
                    existing.capabilities = model.capabilities;
                }
            } else {
                models.push(ModelConfig {
                    id: model.id,
                    capabilities: model.capabilities,
                    price_input: None,
                    price_output: None,
                    currency: "USD".into(),
                });
            }
        }
        if models.len() > 500 {
            return Err("模型超过 500 个，请按供应商划分配置".into());
        }
        cursor = page.next;
        if cursor.is_none() {
            break;
        }
        if page_index == 19 || !seen_cursors.insert(cursor.clone().unwrap()) {
            return Err("模型分页异常或超过 20 页，未覆盖已保存配置".into());
        }
    }
    provider.models = models;
    let provider = state
        .storage
        .upsert_provider(provider)
        .await
        .map_err(|e| e.to_string())?;
    Ok(provider.models)
}

#[tauri::command]
async fn save_settings(state: AppState<'_>, settings: AppSettings) -> Result<AppSettings, String> {
    let _operation = operation(&state).await?;
    mode(&settings)?;
    if let Some(selection) = &settings.default_model {
        let (provider_id, model_id) = selection.split_once(':').ok_or("默认模型需要关联供应商")?;
        let providers = state
            .storage
            .list_providers()
            .await
            .map_err(|e| e.to_string())?;
        if !providers.iter().any(|provider| {
            provider.id == provider_id && provider.models.iter().any(|model| model.id == model_id)
        }) {
            return Err("默认供应商或模型不存在，请重新选择".into());
        }
    }
    state
        .storage
        .save_settings(settings)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn list_jobs(state: AppState<'_>) -> Result<Vec<Job>, String> {
    let shared = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || shared.jobs.list())
        .await
        .map_err(|_| "任务查询中断")?
}

#[tauri::command(rename_all = "snake_case")]
async fn cancel_job(state: AppState<'_>, job_id: String) -> Result<Job, String> {
    let _operation = operation(&state).await?;
    let shared = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || shared.jobs.cancel(&job_id))
        .await
        .map_err(|_| "取消任务中断")?
}

#[tauri::command(rename_all = "snake_case")]
async fn resolve_permission(
    state: AppState<'_>,
    job_id: String,
    digest: String,
    decision: PermissionChoice,
) -> Result<Job, String> {
    let _operation = operation(&state).await?;
    let shared = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || shared.jobs.decide(&job_id, &digest, decision))
        .await
        .map_err(|_| "权限决定中断")?
}

#[tauri::command]
async fn queue_diagnostic_job(state: AppState<'_>, kind: DiagnosticKind) -> Result<Job, String> {
    let _operation = operation(&state).await?;
    if !matches!(kind, DiagnosticKind::Storage | DiagnosticKind::Permission) {
        return Err("请选择诊断类型".into());
    }
    let settings = state
        .storage
        .get_settings()
        .await
        .map_err(|e| e.to_string())?;
    let policy = environment(&state, &settings)?.policy;
    let shared = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || shared.jobs.submit(kind, policy))
        .await
        .map_err(|_| "任务提交中断")?
}

#[tauri::command(rename_all = "snake_case")]
async fn queue_grading_job(
    state: AppState<'_>,
    document_id: String,
    provider_id: String,
    model_id: String,
    thinking: Option<crate::ai::providers::Thinking>,
    allow_unknown: bool,
    reference_only: bool,
) -> Result<Job, String> {
    let _operation = operation(&state).await?;
    let document = state
        .storage
        .get_document(document_id)
        .await
        .map_err(|e| e.to_string())?;
    if document.pending_pdf_pages {
        return Err("请先解析旧材料中的 PDF".into());
    }
    if !document.pages.iter().any(|p| {
        p.role
            == if reference_only {
                "reference"
            } else {
                "material"
            }
    }) {
        return Err("没有对应的输入页".into());
    }
    let provider = state
        .storage
        .list_providers()
        .await
        .map_err(|e| e.to_string())?
        .into_iter()
        .find(|p| p.id == provider_id)
        .ok_or("供应商不存在")?;
    let model = provider
        .models
        .iter()
        .find(|m| m.id == model_id)
        .cloned()
        .ok_or("模型不存在")?;
    let options = crate::ai::providers::RequestOptions {
        model: crate::ai::providers::Model {
            id: model.id.clone(),
            capabilities: model.capabilities.clone(),
            raw: serde_json::Value::Null,
        },
        max_output_tokens: 16384,
        stream: true,
        thinking: thinking.clone(),
        allow_unknown_capabilities: allow_unknown,
        extra: Default::default(),
    };
    options
        .validate(provider.protocol, true, true)
        .map_err(|e| e.to_string())?;
    if provider.key_ref.is_none() {
        return Err("请先保存供应商密钥".into());
    }
    let settings = state
        .storage
        .get_settings()
        .await
        .map_err(|e| e.to_string())?;
    let policy = environment(&state, &settings)?.policy;
    let shared = state.inner().clone();
    tauri::async_runtime::spawn_blocking(move || {
        if shared.jobs.list()?.iter().any(|j| {
            !j.state.is_terminal()
                && j.input
                    .as_ref()
                    .is_some_and(|i| i.document.id == document.id)
        }) {
            return Err("该材料已有未完成任务，请等待或取消".into());
        }
        shared.jobs.submit_input(
            if reference_only {
                DiagnosticKind::References
            } else {
                DiagnosticKind::Grading
            },
            policy,
            Some(GradingInput {
                document,
                provider,
                model,
                thinking,
                allow_unknown,
            }),
        )
    })
    .await
    .map_err(|_| "整卷任务提交中断")?
}

#[tauri::command(rename_all = "snake_case")]
async fn grading_result(state: AppState<'_>, document_id: String) -> Result<GradingResult, String> {
    state
        .storage
        .grading_result(document_id)
        .await
        .map_err(|e| e.to_string())
}
#[tauri::command]
async fn list_questions(state: AppState<'_>) -> Result<Vec<SavedQuestion>, String> {
    state
        .storage
        .list_questions()
        .await
        .map_err(|e| e.to_string())
}
#[tauri::command(rename_all = "snake_case")]
async fn parse_pending_pdfs(state: AppState<'_>, document_id: String) -> Result<Document, String> {
    let _operation = operation(&state).await?;
    state
        .storage
        .parse_pending_pdfs(document_id)
        .await
        .map_err(|e| e.to_string())
}
#[tauri::command(rename_all = "snake_case")]
async fn edit_question(
    state: AppState<'_>,
    id: String,
    question: Question,
) -> Result<SavedQuestion, String> {
    let _operation = operation(&state).await?;
    state
        .storage
        .edit_question(id, question)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
fn subscribe_jobs(state: AppState<'_>, channel: Channel<JobEvent>) {
    let shared = state.inner().clone();
    let generation = shared.subscription.fetch_add(1, Ordering::AcqRel) + 1;
    let mut receiver = shared.jobs.subscribe();
    tauri::async_runtime::spawn(async move {
        while shared.subscription.load(Ordering::Acquire) == generation
            && !shared.shutting_down.is_cancelled()
        {
            match tokio::time::timeout(Duration::from_secs(5), receiver.recv()).await {
                Ok(Ok(event)) => {
                    if channel.send(event).is_err() {
                        break;
                    }
                }
                Ok(Err(tokio::sync::broadcast::error::RecvError::Closed)) => break,
                _ => {} // A lagged consumer recovers through list_jobs snapshots.
            }
        }
    });
}

pub fn run() {
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            let mut data_directory = app.path().app_data_dir()?;
            #[cfg(debug_assertions)]
            if let Some(path) = std::env::var_os("OH_MY_STUDY_DATA_DIR") {
                data_directory = PathBuf::from(path);
            }
            fs::create_dir_all(&data_directory)?;
            let data_directory = data_directory.canonicalize()?;
            let results_root = data_directory.join("jobs");
            fs::create_dir_all(&results_root)?;
            let private_runtime = if cfg!(debug_assertions) {
                PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                    .parent()
                    .unwrap()
                    .join("runtime/staging/python")
            } else {
                app.path().resource_dir()?.join("runtime/python")
            };
            let pdfium_library = if cfg!(debug_assertions) {
                PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("resources/pdfium/bin/pdfium.dll")
            } else {
                app.path()
                    .resource_dir()?
                    .join("resources/pdfium/bin/pdfium.dll")
            };
            let storage = Storage::open_with_pdfium(&data_directory, pdfium_library)?;
            let jobs = JobManager::with_runner(
                Arc::new(storage.clone()),
                JobConfig {
                    private_runtime,
                    results_root,
                },
                Arc::new(crate::ai::grading::GradingRunner {
                    storage: storage.clone(),
                }),
            )?;
            app.manage(Arc::new(DesktopState {
                storage,
                jobs,
                data_directory,
                capability: Capability {
                    state: CapabilityState::Unknown,
                    reason: "Windows Sandbox 自动启停与会话归属尚未验收；当前代码在本机运行".into(),
                },
                selected_paths: Mutex::new(HashSet::new()),
                subscription: AtomicU64::new(0),
                provider_mutation: tokio::sync::Mutex::new(()),
                shutting_down: CancellationToken::new(),
                operations: tokio::sync::RwLock::new(()),
                shutdown_complete: AtomicBool::new(false),
            }));
            Ok(())
        })
        .on_window_event(|window, event| {
            if let tauri::WindowEvent::DragDrop(tauri::DragDropEvent::Drop { paths, .. }) = event {
                if let Some(state) = window.try_state::<Arc<DesktopState>>() {
                    if let Ok(mut selected) = state.selected_paths.lock() {
                        for path in paths {
                            if let Ok(path) = checked_input(path) {
                                selected.insert(path);
                            }
                        }
                    }
                }
            }
        })
        .invoke_handler(tauri::generate_handler![
            bootstrap,
            list_documents,
            select_import_files,
            import_files,
            append_reference_files,
            update_pages,
            read_asset,
            save_provider,
            refresh_models,
            save_settings,
            list_jobs,
            cancel_job,
            resolve_permission,
            queue_diagnostic_job,
            subscribe_jobs,
            queue_grading_job,
            grading_result,
            list_questions,
            edit_question,
            parse_pending_pdfs
        ])
        .build(tauri::generate_context!())
        .expect("Oh My Study desktop initialization failed");
    app.run(|app, event| {
        if let tauri::RunEvent::ExitRequested { api, .. } = event {
            let state = app.state::<Arc<DesktopState>>().inner().clone();
            if state.shutdown_complete.load(Ordering::Acquire) {
                return;
            }
            api.prevent_exit();
            if state.shutting_down.is_cancelled() {
                return;
            }
            state.shutting_down.cancel();
            let handle = app.clone();
            tauri::async_runtime::spawn(async move {
                let _drain = state.operations.write().await;
                let worker_state = state.clone();
                let _ = tauri::async_runtime::spawn_blocking(move || {
                    let _ = worker_state.jobs.shutdown();
                    // The queue barrier ensures earlier database operations have returned.
                    let _ = worker_state.storage.verify_integrity();
                })
                .await;
                state.shutdown_complete.store(true, Ordering::Release);
                handle.exit(0);
            });
        }
    });
}
