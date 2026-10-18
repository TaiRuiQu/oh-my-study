import { useRef, useState } from 'react';
import {
  Check,
  ChevronRight,
  CircleHelp,
  Cpu,
  Eye,
  KeyRound,
  Layers3,
  Plus,
  RefreshCw,
  Save,
  Server,
  Settings2,
  Shield,
  ShieldAlert,
  ShieldCheck,
  Trash2,
} from 'lucide-react';
import type { FormEvent } from 'react';
import type { PageProps } from '../app/App';
import { api, desktopAvailable } from '../lib/api';
import { executionLabels } from '../lib/labels';
import type {
  ExecutionMode,
  ModelConfig,
  Protocol,
  Provider,
  Settings,
  Thinking,
} from '../lib/api';
import { Button, EmptyState, Notice } from '../components/ui';

const protocolLabels: Record<Protocol, string> = {
  responses: 'OpenAI Responses',
  messages: 'Anthropic Messages',
  gemini: 'Gemini 原生 API',
};
const protocolUrls: Record<Protocol, string> = {
  responses: 'https://api.openai.com/v1',
  messages: 'https://api.anthropic.com/v1',
  gemini: 'https://generativelanguage.googleapis.com/v1beta',
};
function newProvider(): Provider {
  return {
    id: crypto.randomUUID(),
    name: '',
    protocol: 'responses',
    base_url: protocolUrls.responses,
    key_ref: null,
    has_key: false,
    models: [],
  };
}
function newModel(): ModelConfig {
  return {
    id: '',
    capabilities: {
      vision: null,
      tools: null,
      thinking: [],
      messages_budget: null,
      source: 'manual',
    },
    price_input: null,
    price_output: null,
    currency: 'USD',
  };
}

export function SettingsPage({ data, onChanged }: PageProps) {
  const [tab, setTab] = useState<'models' | 'execution'>('models');
  const [editing, setEditing] = useState<string | null>(data?.providers[0]?.id ?? null);
  const [creating, setCreating] = useState(false);
  const [draftId, setDraftId] = useState(0);
  const provider = data?.providers.find((item) => item.id === editing);
  return (
    <div className="page settings-page">
      <div className="page-heading">
        <div>
          <span className="eyebrow">让工具适合你的习惯</span>
          <h1>
            设置<span className="heading-dot">.</span>
          </h1>
          <p>管理模型连接，以及计算任务的执行方式。</p>
        </div>
        <span className="outline-badge">
          <Settings2 size={14} />
          本地配置
        </span>
      </div>
      <div className="tabs" role="tablist" aria-label="设置分类">
        <button
          role="tab"
          aria-selected={tab === 'models'}
          className={tab === 'models' ? 'active' : ''}
          onClick={() => setTab('models')}
        >
          <Cpu size={16} />
          供应商与模型
        </button>
        <button
          role="tab"
          aria-selected={tab === 'execution'}
          className={tab === 'execution' ? 'active' : ''}
          onClick={() => setTab('execution')}
        >
          <Shield size={16} />
          执行与权限
        </button>
      </div>
      {tab === 'models' ? (
        <div className="settings-grid">
          <aside className="panel provider-list">
            <div className="panel-heading">
              <h2>模型供应商</h2>
              <span className="subtle">{data?.providers.length ?? 0}</span>
            </div>
            {data?.providers.map((item) => (
              <button
                key={item.id}
                className={`provider-option ${item.id === editing && !creating ? 'active' : ''}`}
                onClick={() => {
                  setEditing(item.id);
                  setCreating(false);
                }}
              >
                <Server size={17} />
                <span>
                  <strong>{item.name}</strong>
                  <small>{protocolLabels[item.protocol]}</small>
                </span>
                <ChevronRight size={14} />
              </button>
            ))}
            <Button
              variant="ghost"
              disabled={!desktopAvailable}
              onClick={() => {
                setCreating(true);
                setEditing(null);
                setDraftId((value) => value + 1);
              }}
            >
              <Plus size={16} />
              添加供应商
            </Button>
            <p className="provider-help">
              <CircleHelp size={15} />
              三种原生协议分别处理。兼容服务需明确选择实际支持的协议。
            </p>
          </aside>
          {provider || creating ? (
            <ProviderEditor
              key={creating ? `new-${draftId}` : provider!.id}
              initial={provider ?? newProvider()}
              onChanged={async (saved) => {
                await onChanged();
                setCreating(false);
                setEditing(saved.id);
              }}
            />
          ) : (
            <section className="panel provider-empty">
              <EmptyState title="连接你自己的模型">
                应用可以离线启动。需要 AI 功能时，再添加供应商地址、模型与密钥。
              </EmptyState>
              <Button
                disabled={!desktopAvailable}
                onClick={() => {
                  setCreating(true);
                  setDraftId((value) => value + 1);
                }}
              >
                <Plus size={16} />
                添加供应商
              </Button>
              <div className="protocol-chips">
                {Object.values(protocolLabels).map((label) => (
                  <span key={label}>{label}</span>
                ))}
              </div>
            </section>
          )}
        </div>
      ) : (
        <ExecutionSettings data={data} onChanged={onChanged} />
      )}
    </div>
  );
}

function ProviderEditor({
  initial,
  onChanged,
}: {
  initial: Provider;
  onChanged: (provider: Provider) => Promise<void>;
}) {
  const [provider, setProvider] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const keyInput = useRef<HTMLInputElement>(null);
  const [saved, setSaved] = useState(!!initial.name);
  const [savedConnection, setSavedConnection] = useState({
    protocol: initial.protocol,
    base_url: initial.base_url,
  });
  const connectionSaved =
    saved &&
    savedConnection.protocol === provider.protocol &&
    savedConnection.base_url === provider.base_url;
  function setModel(index: number, model: ModelConfig) {
    setProvider({
      ...provider,
      models: provider.models.map((item, i) => (i === index ? model : item)),
    });
  }
  async function save(event: FormEvent) {
    event.preventDefault();
    setError('');
    setMessage('');
    setBusy(true);
    // Never put the credential in component state, query variables, logs, or storage.
    const key = keyInput.current?.value;
    if (keyInput.current) keyInput.current.value = '';
    try {
      const result = await api.saveProvider(provider, key);
      setProvider(result);
      setSaved(true);
      setSavedConnection({ protocol: result.protocol, base_url: result.base_url });
      await onChanged(result);
      setMessage('供应商配置已保存。密钥由系统凭据存储管理。');
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function refresh() {
    setRefreshing(true);
    setError('');
    setMessage('');
    try {
      const models = await api.refreshModels(provider.id);
      setProvider({ ...provider, models });
      await onChanged({ ...provider, models });
      setMessage(`模型列表已更新，共 ${models.length} 个模型。未知能力与价格需要人工核对。`);
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setRefreshing(false);
    }
  }
  return (
    <form
      className="panel provider-editor"
      onSubmit={(event) => void save(event)}
      autoComplete="off"
    >
      <div className="panel-heading">
        <h2>
          <Server size={18} />
          {initial.name || '添加模型供应商'}
        </h2>
        <span className={`key-status ${provider.has_key ? 'configured' : ''}`}>
          <KeyRound size={13} />
          {provider.has_key ? '密钥已设置' : '未设置密钥'}
        </span>
      </div>
      <div className="provider-fields">
        <div className="field-grid">
          <label className="field">
            <span>供应商名称</span>
            <input
              value={provider.name}
              required
              maxLength={200}
              placeholder="例如：我的模型服务"
              onChange={(event) => setProvider({ ...provider, name: event.target.value })}
            />
          </label>
          <label className="field">
            <span>原生协议</span>
            <select
              value={provider.protocol}
              onChange={(event) => {
                const protocol = event.target.value as Protocol;
                setProvider({
                  ...provider,
                  protocol,
                  base_url: protocolUrls[protocol],
                  models: [],
                });
              }}
            >
              <option value="responses">OpenAI Responses</option>
              <option value="messages">Anthropic Messages</option>
              <option value="gemini">Gemini 原生 API</option>
            </select>
          </label>
        </div>
        <label className="field">
          <span>API 地址</span>
          <input
            value={provider.base_url}
            required
            type="url"
            placeholder={protocolUrls[provider.protocol]}
            onChange={(event) => setProvider({ ...provider, base_url: event.target.value })}
          />
          <small>填写协议根地址，Rust 负责请求和认证。地址中不要包含密钥。</small>
        </label>
        <label className="field">
          <span>
            <KeyRound size={13} />
            API 密钥
            <span className="subtle">{provider.has_key ? '留空保留现有密钥' : '可稍后设置'}</span>
          </span>
          <input
            ref={keyInput}
            type="password"
            autoComplete="new-password"
            spellCheck={false}
            placeholder={provider.has_key ? '输入新密钥以替换' : '输入供应商提供的密钥'}
            maxLength={16384}
          />
          <small>提交后输入框立即清空。密钥不进入前端状态、日志或普通备份。</small>
        </label>
      </div>
      {error && (
        <div className="form-notice">
          <Notice kind="error">{error}</Notice>
        </div>
      )}
      {message && (
        <div className="form-notice">
          <Notice kind="success">{message}</Notice>
        </div>
      )}
      <div className="models-heading">
        <div>
          <h3>
            模型配置<span>{provider.models.length}</span>
          </h3>
          <p>未知能力保持“未知”，不按模型名称推断。</p>
        </div>
        <Button
          type="button"
          variant="secondary"
          disabled={!connectionSaved || busy || !desktopAvailable}
          busy={refreshing}
          onClick={() => void refresh()}
        >
          <RefreshCw size={14} />
          获取模型列表
        </Button>
      </div>
      <div className="models-list">
        {provider.models.map((model, index) => (
          <ModelEditor
            key={index}
            model={model}
            protocol={provider.protocol}
            onChange={(value) => setModel(index, value)}
            onRemove={() =>
              setProvider({ ...provider, models: provider.models.filter((_, i) => i !== index) })
            }
          />
        ))}
        {!provider.models.length && (
          <div className="models-empty">
            <Cpu size={22} />
            <strong>还没有模型</strong>
            <p>保存供应商后获取列表，或手动添加模型 ID。</p>
          </div>
        )}
        <Button
          variant="ghost"
          type="button"
          onClick={() => setProvider({ ...provider, models: [...provider.models, newModel()] })}
        >
          <Plus size={15} />
          手动添加模型
        </Button>
      </div>
      <div className="form-footer">
        <span>
          <ShieldCheck size={14} />
          配置保存在本机
        </span>
        <Button type="submit" busy={busy} disabled={!desktopAvailable || refreshing}>
          <Save size={15} />
          保存配置
        </Button>
      </div>
    </form>
  );
}

function ModelEditor({
  model,
  protocol,
  onChange,
  onRemove,
}: {
  model: ModelConfig;
  protocol: Protocol;
  onChange: (model: ModelConfig) => void;
  onRemove: () => void;
}) {
  function capability(key: 'vision' | 'tools', value: string) {
    onChange({
      ...model,
      capabilities: {
        ...model.capabilities,
        [key]: value === 'unknown' ? null : value === 'true',
        source: 'manual',
      },
    });
  }
  function updateThinking(index: number, value: Thinking) {
    onChange({
      ...model,
      capabilities: {
        ...model.capabilities,
        thinking: model.capabilities.thinking.map((item, i) => (i === index ? value : item)),
        source: 'manual',
      },
    });
  }
  function addThinking() {
    const value: Thinking =
      protocol === 'responses'
        ? { kind: 'responses', effort: 'medium' }
        : protocol === 'messages'
          ? { kind: 'adaptive', effort: 'medium' }
          : { kind: 'gemini_level', level: 'high' };
    onChange({
      ...model,
      capabilities: {
        ...model.capabilities,
        thinking: [...model.capabilities.thinking, value],
        source: 'manual',
      },
    });
  }
  return (
    <div className="model-card">
      <div className="model-id">
        <Cpu size={17} />
        <input
          aria-label="模型 ID"
          value={model.id}
          placeholder="输入准确的模型 ID"
          required
          maxLength={512}
          onChange={(event) => onChange({ ...model, id: event.target.value })}
        />
        <button type="button" className="icon-button" aria-label="删除模型配置" onClick={onRemove}>
          <Trash2 size={14} />
        </button>
      </div>
      <div className="model-capabilities">
        <label>
          <span>
            <Eye size={13} />
            图片理解
          </span>
          <select
            value={
              model.capabilities.vision === null ? 'unknown' : String(model.capabilities.vision)
            }
            onChange={(event) => capability('vision', event.target.value)}
          >
            <option value="unknown">未知</option>
            <option value="true">支持</option>
            <option value="false">不支持</option>
          </select>
        </label>
        <label>
          <span>
            <Layers3 size={13} />
            工具调用
          </span>
          <select
            value={model.capabilities.tools === null ? 'unknown' : String(model.capabilities.tools)}
            onChange={(event) => capability('tools', event.target.value)}
          >
            <option value="unknown">未知</option>
            <option value="true">支持</option>
            <option value="false">不支持</option>
          </select>
        </label>
      </div>
      <div className="thinking-section">
        <div>
          <span>原生思考参数</span>
          <button type="button" className="text-button" onClick={addThinking}>
            <Plus size={12} />
            添加
          </button>
        </div>
        {model.capabilities.thinking.length === 0 && <small>未配置 · 不自动发送思考参数</small>}
        {model.capabilities.thinking.map((thinking, index) => (
          <div className="thinking-row" key={index}>
            <select
              aria-label="思考参数类型"
              value={thinking.kind}
              onChange={(event) => {
                const kind = event.target.value;
                updateThinking(
                  index,
                  kind === 'responses' || kind === 'adaptive'
                    ? { kind, effort: 'medium' }
                    : kind === 'gemini_level'
                      ? { kind, level: 'high' }
                      : { kind: kind as 'budget' | 'gemini_budget', tokens: 1024 },
                );
              }}
            >
              {protocol === 'responses' && <option value="responses">reasoning.effort</option>}
              {protocol === 'messages' && (
                <>
                  <option value="adaptive">adaptive effort</option>
                  <option value="budget">budget_tokens</option>
                </>
              )}
              {protocol === 'gemini' && (
                <>
                  <option value="gemini_level">thinkingLevel</option>
                  <option value="gemini_budget">thinkingBudget</option>
                </>
              )}
            </select>
            {'tokens' in thinking ? (
              <input
                aria-label="思考预算 tokens"
                type="number"
                min={thinking.kind === 'gemini_budget' ? -1 : 1}
                step="1"
                value={thinking.tokens}
                onChange={(event) =>
                  updateThinking(index, { ...thinking, tokens: Number(event.target.value) })
                }
              />
            ) : (
              <input
                aria-label="思考强度"
                required
                value={'effort' in thinking ? thinking.effort : thinking.level}
                onChange={(event) =>
                  updateThinking(
                    index,
                    'effort' in thinking
                      ? { ...thinking, effort: event.target.value }
                      : { ...thinking, level: event.target.value },
                  )
                }
              />
            )}
            <button
              type="button"
              className="icon-button"
              aria-label="删除思考参数"
              onClick={() =>
                onChange({
                  ...model,
                  capabilities: {
                    ...model.capabilities,
                    thinking: model.capabilities.thinking.filter((_, i) => i !== index),
                  },
                })
              }
            >
              <Trash2 size={13} />
            </button>
          </div>
        ))}
      </div>
      <div className="pricing-row">
        <label>
          输入 / 百万 tokens
          <input
            aria-label="输入价格每百万 tokens"
            type="number"
            step="any"
            min="0"
            placeholder="未配置"
            value={model.price_input ?? ''}
            onChange={(event) =>
              onChange({
                ...model,
                price_input: event.target.value === '' ? null : Number(event.target.value),
              })
            }
          />
        </label>
        <label>
          输出 / 百万 tokens
          <input
            aria-label="输出价格每百万 tokens"
            type="number"
            step="any"
            min="0"
            placeholder="未配置"
            value={model.price_output ?? ''}
            onChange={(event) =>
              onChange({
                ...model,
                price_output: event.target.value === '' ? null : Number(event.target.value),
              })
            }
          />
        </label>
        <label>
          币种
          <input
            aria-label="价格币种"
            required
            maxLength={16}
            value={model.currency}
            onChange={(event) => onChange({ ...model, currency: event.target.value })}
          />
        </label>
      </div>
    </div>
  );
}

function ExecutionSettings({ data, onChanged }: PageProps) {
  const [settings, setSettings] = useState<Settings>(
    data?.settings ?? {
      execution_mode: 'request_permission',
      default_model: null,
      sandbox_networking: false,
    },
  );
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);
  const policies: {
    value: ExecutionMode;
    icon: typeof Shield;
    title: string;
    description: string;
  }[] = [
    {
      value: 'request_permission',
      icon: ShieldCheck,
      title: '请求权限',
      description: '代码或命令执行前，逐次审阅；也可允许当前任务后续执行。',
    },
    {
      value: 'sandbox',
      icon: Shield,
      title: '沙箱运行',
      description: 'Windows Sandbox 可用时在沙箱运行；不可用时明确显示本机执行警告。',
    },
    {
      value: 'bypass_permission',
      icon: ShieldAlert,
      title: '绕过权限',
      description: '直接使用当前账户在本机执行，能够访问该账户的文件和网络。',
    },
  ];
  async function save() {
    setBusy(true);
    setError('');
    setSaved(false);
    try {
      await api.saveSettings(settings);
      await onChanged();
      setSaved(true);
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="execution-content">
      <section className="panel execution-panel">
        <div className="panel-heading">
          <h2>
            <Shield size={18} />
            计算执行方式
          </h2>
          <span>新设置应用于新任务</span>
        </div>
        <div className="execution-options" role="radiogroup" aria-label="执行方式">
          {policies.map(({ value, icon: Icon, title, description }) => (
            <button
              className={`execution-option ${settings.execution_mode === value ? 'selected' : ''}`}
              key={value}
              disabled={value === 'sandbox' && data?.environment.capability.state !== 'ready'}
              role="radio"
              aria-checked={settings.execution_mode === value}
              onClick={() => {
                setSettings({ ...settings, execution_mode: value });
                setSaved(false);
              }}
            >
              <span className="mode-icon">
                <Icon size={22} />
              </span>
              <span>
                <strong>{title}</strong>
                <small>{description}</small>
              </span>
              <span className="radio-mark">
                {settings.execution_mode === value && <Check size={10} strokeWidth={3} />}
              </span>
            </button>
          ))}
        </div>
        <div className="network-setting">
          <label>
            <input
              type="checkbox"
              checked={settings.sandbox_networking}
              onChange={(event) =>
                setSettings({ ...settings, sandbox_networking: event.target.checked })
              }
            />
            允许 Windows Sandbox 联网
          </label>
          <p>此选项只影响沙箱；本机模式使用当前账户的网络权限。</p>
        </div>
        <div className="default-model-setting">
          <label className="field">
            <span>默认模型</span>
            <select
              aria-label="默认模型"
              value={settings.default_model ?? ''}
              onChange={(event) =>
                setSettings({ ...settings, default_model: event.target.value || null })
              }
            >
              <option value="">暂不选择</option>
              {data?.providers.flatMap((provider) =>
                provider.models.map((model) => (
                  <option key={`${provider.id}:${model.id}`} value={`${provider.id}:${model.id}`}>
                    {provider.name} / {model.id}
                  </option>
                )),
              )}
            </select>
          </label>
        </div>
        {error && (
          <div className="form-notice">
            <Notice kind="error">{error}</Notice>
          </div>
        )}
        {saved && (
          <div className="form-notice">
            <Notice kind="success">执行设置已保存。</Notice>
          </div>
        )}
        <div className="form-footer">
          <span>不会自动提权或修改系统功能</span>
          <Button busy={busy} disabled={!desktopAvailable} onClick={() => void save()}>
            <Save size={15} />
            保存设置
          </Button>
        </div>
      </section>
      <section className="panel environment-panel">
        <div className="panel-heading">
          <h2>当前环境</h2>
        </div>
        <div className="environment-body">
          <span className="environment-symbol">
            <ShieldCheck size={27} />
          </span>
          <h3>{data ? executionLabels[data.environment.policy.effective_mode] : '等待桌面环境'}</h3>
          <p>
            {data
              ? data.environment.policy.executor_kind === 'windows_sandbox'
                ? 'Windows Sandbox'
                : '本机执行 · 当前账户权限 · 未隔离'
              : '浏览器预览不检测系统执行环境'}
          </p>
          {data && (
            <>
              <dl>
                <div>
                  <dt>沙箱检测</dt>
                  <dd>{data.environment.capability.state}</dd>
                </div>
                <div>
                  <dt>请求方式</dt>
                  <dd>{executionLabels[data.environment.policy.preferred_mode]}</dd>
                </div>
              </dl>
              <Notice kind={data.environment.policy.warning ? 'warning' : 'info'}>
                {data.environment.policy.warning ?? data.environment.capability.reason}
              </Notice>
              <div className="data-directory">
                <strong>数据目录</strong>
                <code>{data.environment.data_directory}</code>
              </div>
            </>
          )}
        </div>
      </section>
    </div>
  );
}
