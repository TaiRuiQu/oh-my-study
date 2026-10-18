import { lazy, Suspense, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Badge,
  Checkbox,
  Field,
  Input,
  Select,
  Textarea,
  ProgressBar,
} from '@fluentui/react-components';
import {
  PlayRegular,
  ArrowSyncRegular,
  EditRegular,
  DocumentSearchRegular,
} from '@fluentui/react-icons';
import { Link } from 'react-router-dom';
import { api, desktopAvailable } from '../lib/api';
import type {
  Bootstrap,
  Content,
  Grade,
  Question,
  Region,
  SavedQuestion,
  SourceDocument,
  Verdict,
} from '../lib/api';
import { Button, EmptyState, Modal, Notice } from '../components/ui';
const LazyFormula = lazy(() =>
  import('../components/formula').then((module) => ({ default: module.Formula })),
);
function Formula({ latex }: { latex: string }) {
  return (
    <Suspense fallback={<code>{latex}</code>}>
      <LazyFormula latex={latex} />
    </Suspense>
  );
}

const verdicts: Record<Verdict, string> = {
  correct: '正确',
  partial: '部分正确',
  incorrect: '错误',
  unanswered: '未作答',
  uncertain: '无法判定',
};
const provenance: Record<Grade['source'], string> = {
  teacher: '教师批改',
  ai_reference: 'AI 按参考答案',
  ai_solution: 'AI 自行解题',
  manual: '手动修改',
};
const active = (state: string) => ['QUEUED', 'RUNNING', 'WAITING_PERMISSION'].includes(state);

function flatten(question: Question): Question[] {
  return [question, ...question.parts.flatMap(flatten)];
}

export function GradingPanel({
  document,
  data,
  onSource,
  onChanged,
}: {
  document: SourceDocument;
  data: Bootstrap | undefined;
  onSource: (region: Region) => void;
  onChanged: () => Promise<void>;
}) {
  const client = useQueryClient();
  const [selection, setSelection] = useState(data?.settings.default_model ?? '');
  const [thinking, setThinking] = useState('');
  const [allowUnknown, setAllowUnknown] = useState(false);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const jobs = useQuery({
    queryKey: ['jobs'],
    queryFn: api.listJobs,
    enabled: desktopAvailable,
    refetchInterval: 2500,
  });
  const job = jobs.data?.find((j) => j.input?.document.id === document.id);
  const running = !!job && active(job.state);
  const result = useQuery({
    queryKey: ['grading', document.id],
    queryFn: () => api.gradingResult(document.id),
    enabled: desktopAvailable,
    refetchInterval: 2500,
  });
  const options =
    data?.providers.flatMap((provider) =>
      provider.models.map((model) => ({ provider, model, value: `${provider.id}:${model.id}` })),
    ) ?? [];
  const chosen = options.find((o) => o.value === selection);
  async function start(referenceOnly = false) {
    if (!chosen) return;
    setBusy(true);
    setError('');
    try {
      await api.queueGrading(
        document.id,
        chosen.provider.id,
        chosen.model.id,
        thinking === '' ? null : chosen.model.capabilities.thinking[Number(thinking)],
        allowUnknown,
        referenceOnly,
      );
      await onChanged();
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function parse() {
    setBusy(true);
    setError('');
    try {
      await api.parsePdfs(document.id);
      await onChanged();
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="grading-content">
      {error && <Notice kind="error">{error}</Notice>}
      {document.pending_pdf_pages && (
        <Notice kind="warning">
          这份旧材料含未解析 PDF。
          <Button busy={busy} onClick={() => void parse()}>
            解析 PDF 页面
          </Button>
        </Notice>
      )}
      <Field label="整卷处理模型">
        <Select
          value={selection}
          disabled={running}
          onChange={(_, value) => {
            setSelection(value.value);
            setThinking('');
            setAllowUnknown(false);
          }}
        >
          <option value="">选择供应商和模型</option>
          {options.map((o) => (
            <option key={o.value} value={o.value}>
              {o.provider.name} · {o.model.id}
            </option>
          ))}
        </Select>
      </Field>
      {chosen && (
        <>
          <Field label="思考配置">
            <Select
              disabled={running}
              value={thinking}
              onChange={(_, value) => setThinking(value.value)}
            >
              <option value="">供应商默认</option>
              {chosen.model.capabilities.thinking.map((item, index) => (
                <option key={index} value={index}>
                  {item.kind}:{' '}
                  {'effort' in item ? item.effort : 'level' in item ? item.level : item.tokens}
                </option>
              ))}
            </Select>
          </Field>
          {(chosen.model.capabilities.vision === null ||
            chosen.model.capabilities.tools === null) && (
            <Checkbox
              disabled={running}
              checked={allowUnknown}
              onChange={(_, value) => setAllowUnknown(value.checked === true)}
              label="能力未知，允许主动测试此模型的视觉和工具调用"
            />
          )}
        </>
      )}
      <div className="grading-actions">
        <Button
          busy={busy}
          disabled={running || !chosen || document.pending_pdf_pages}
          onClick={() => void start()}
        >
          <PlayRegular />
          {result.data?.questions.length
            ? '重新批改整卷'
            : document.mode === 'blank'
              ? '识别空白试卷'
              : '开始整卷识别与批改'}
        </Button>
        {document.pages.some((p) => p.role === 'reference') && (
          <Button
            variant="secondary"
            busy={busy}
            disabled={running || !chosen || document.pending_pdf_pages}
            onClick={() => void start(true)}
          >
            <DocumentSearchRegular />
            整理参考答案与匹配
          </Button>
        )}
      </div>
      <p className="field-hint">
        点击开始会将材料发送给所选供应商，并产生模型费用。后补答案仅保存附件；请先核对匹配，再点击重新批改。手动分数与明确教师结果会保留。
      </p>
      {job?.kind === 'references' && job.state === 'COMPLETED' && (
        <Notice kind="warning">
          参考答案匹配已更新，题目评分尚未重新批改。请核对匹配后点击“重新批改整卷”。
        </Notice>
      )}
      {job && (
        <div className="grading-status" aria-live="polite">
          <strong>{job.message}</strong>
          {running && <ProgressBar value={job.progress / 100} />}
          {job.error && <Notice kind="error">{job.error}</Notice>}
          {running && (
            <Button
              variant="secondary"
              onClick={() =>
                void api
                  .cancelJob(job.id)
                  .then(onChanged)
                  .catch((cause) => setError((cause as Error).message))
              }
            >
              取消任务
            </Button>
          )}
        </div>
      )}
      {result.error && <Notice kind="error">{result.error.message}</Notice>}
      {!!result.data?.references.length && (
        <details className="result-section" open>
          <summary>参考答案匹配 · {result.data.references.length} 条</summary>
          {result.data.references.map((ref) => (
            <div className="reference-match" key={ref.key}>
              <strong>
                {ref.number} → {ref.matched_draft_key ?? '未匹配'}
              </strong>
              <p>{ref.answer}</p>
              {ref.uncertain_reason && <Notice kind="warning">{ref.uncertain_reason}</Notice>}
              <Button variant="ghost" onClick={() => onSource(ref.sources[0])}>
                查看答案原页
              </Button>
            </div>
          ))}
        </details>
      )}
      {!!result.data?.coverage.length && (
        <details className="result-section">
          <summary>逐页覆盖检查 · {result.data.coverage.length} 页</summary>
          {result.data.coverage.map((c) => (
            <p key={c.page_id}>
              第 {document.pages.findIndex((p) => p.id === c.page_id) + 1} 页：
              {c.status === 'covered'
                ? `已检查 ${c.question_keys.length} 个题目／小问`
                : c.status === 'unreadable'
                  ? '无法识别'
                  : '无题目'}
              {c.reason && ` · ${c.reason}`}
            </p>
          ))}
        </details>
      )}
      {result.data?.questions.length ? (
        result.data.questions.map((q) => (
          <QuestionCard
            key={q.id}
            saved={q}
            onSource={onSource}
            onSaved={async () => {
              await client.invalidateQueries({ queryKey: ['grading', document.id] });
              await client.invalidateQueries({ queryKey: ['questions'] });
            }}
            disabled={running}
          />
        ))
      ) : (
        <EmptyState title="尚无识别结果">
          配置模型后开始整卷处理。无法识别的部分会保留原图与原因。
        </EmptyState>
      )}
      {!!result.data?.model_calls.length && (
        <details className="result-section">
          <summary>模型用量与费用 · {result.data.model_calls.length} 次成功调用</summary>
          {result.data.model_calls.map((call, index) => (
            <p key={index}>
              {call.model} · 输入 {call.usage.input ?? '未知'} / 输出 {call.usage.output ?? '未知'}{' '}
              tokens ·{' '}
              {call.estimated_cost === null
                ? '费用未知'
                : `估算 ${call.estimated_cost.toFixed(4)} ${call.currency}`}
            </p>
          ))}
          <p className="field-hint">
            估算按调用时配置的输入／输出单价计算，含缓存输入；实际费用以供应商账单为准。
          </p>
        </details>
      )}
    </div>
  );
}

function ContentView({
  items,
  onSource,
}: {
  items: Content[];
  onSource?: (region: Region) => void;
}) {
  return (
    <>
      {items.map((item, index) =>
        item.kind === 'text' ? (
          <p className="question-text" key={index}>
            {item.text}
          </p>
        ) : item.kind === 'formula' ? (
          <Formula key={index} latex={item.latex} />
        ) : item.kind === 'table' ? (
          <div className="table-scroll" key={index}>
            <table className="question-table">
              <tbody>
                {item.rows.map((row, r) => (
                  <tr key={r}>
                    {row.map((cell, c) => (
                      <td key={c}>{cell}</td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <div key={index}>
            <Button
              variant="secondary"
              disabled={!onSource}
              onClick={() => onSource?.(item.region)}
            >
              查看题图：{item.caption || '原图区域'}
            </Button>
            {item.contains_handwriting && (
              <Badge appearance="outline" color="warning">
                含笔迹
              </Badge>
            )}
          </div>
        ),
      )}
    </>
  );
}

export function QuestionCard({
  saved,
  onSource,
  onSaved,
  disabled = false,
}: {
  saved: SavedQuestion;
  onSource?: (region: Region) => void;
  onSaved: () => Promise<void>;
  disabled?: boolean;
}) {
  const [editing, setEditing] = useState(false);
  return (
    <article className="question-card">
      {flatten(saved.question).map((q, index) => (
        <section key={q.draft_key} className={index ? 'question-part' : ''}>
          <div className="question-title">
            <strong>
              {q.number} {q.subject} · {q.question_type}
            </strong>
            {q.attempt && (
              <Badge
                appearance="tint"
                color={
                  q.attempt.grade.verdict === 'correct'
                    ? 'success'
                    : q.attempt.grade.verdict === 'uncertain'
                      ? 'warning'
                      : 'danger'
                }
              >
                {verdicts[q.attempt.grade.verdict]}
                {q.attempt.grade.earned !== null &&
                  ` ${q.attempt.grade.earned}/${q.attempt.grade.possible}`}
              </Badge>
            )}
          </div>
          <ContentView items={q.content} onSource={onSource} />
          {q.options.map((option, i) => (
            <p key={i}>{option}</p>
          ))}
          {q.sources.map((region, i) => (
            <Button key={i} variant="ghost" disabled={!onSource} onClick={() => onSource?.(region)}>
              来源 {i + 1}
            </Button>
          ))}
          {q.uncertainties.map((text, i) => (
            <Notice key={i} kind="warning">
              {text}
            </Notice>
          ))}
          {q.attempt && (
            <div className="attempt-result">
              <p>
                <strong>学生作答：</strong>
                {q.attempt.student_answer || '未识别出文字，请查看原图'}
              </p>
              {q.attempt.teacher && (
                <p>
                  <strong>教师批注：</strong>
                  {q.attempt.teacher.raw}
                </p>
              )}
              <p>
                <strong>{provenance[q.attempt.grade.source]}：</strong>
                {q.attempt.grade.reason}
              </p>
              {q.attempt.conflict && <Notice kind="warning">{q.attempt.conflict}</Notice>}
            </div>
          )}
          <details>
            <summary>答案、解析与知识点</summary>
            <p>标准答案：{q.standard_answer || '未提供'}</p>
            <p>{q.explanation}</p>
            {q.attempt?.ai_solution && <p>AI 自行解题：{q.attempt.ai_solution}</p>}
            <p>{q.knowledge_points.join(' · ')}</p>
          </details>
        </section>
      ))}
      <Button variant="secondary" disabled={disabled} onClick={() => setEditing(true)}>
        <EditRegular />
        编辑题目与批改
      </Button>
      {editing && (
        <QuestionEditor saved={saved} onClose={() => setEditing(false)} onSaved={onSaved} />
      )}
    </article>
  );
}

function QuestionEditor({
  saved,
  onClose,
  onSaved,
}: {
  saved: SavedQuestion;
  onClose: () => void;
  onSaved: () => Promise<void>;
}) {
  const [question, setQuestion] = useState(() => structuredClone(saved.question));
  const [advanced, setAdvanced] = useState(false);
  const [json, setJson] = useState(JSON.stringify(saved.question, null, 2));
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  function patch(key: string, values: Partial<Question>) {
    setQuestion((previous) => {
      const next = structuredClone(previous);
      const q = flatten(next).find((q) => q.draft_key === key)!;
      Object.assign(q, values);
      return next;
    });
  }
  async function save() {
    setBusy(true);
    setError('');
    try {
      await api.editQuestion(saved.id, advanced ? (JSON.parse(json) as Question) : question);
      await onSaved();
      onClose();
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <Modal
      open
      onOpenChange={(value) => {
        if (!value && !busy) onClose();
      }}
      title="编辑题目与批改"
      description="直接更新当前内容；保存时校验分数与小问合计，不保留修改历史。"
    >
      {error && <Notice kind="error">{error}</Notice>}
      <Checkbox
        checked={advanced}
        onChange={(_, v) => {
          setAdvanced(v.checked === true);
          if (v.checked) setJson(JSON.stringify(question, null, 2));
          else {
            try {
              setQuestion(JSON.parse(json) as Question);
            } catch {
              setError('结构内容不是有效 JSON，请先修正');
              setAdvanced(true);
            }
          }
        }}
        label="编辑完整结构（表格、配图、来源区域和小问）"
      />
      {advanced ? (
        <Field label="题目结构">
          <Textarea
            className="structure-editor"
            value={json}
            onChange={(_, v) => setJson(v.value)}
          />
        </Field>
      ) : (
        flatten(question).map((q) => (
          <div className="question-editor" key={q.draft_key}>
            <h3>{q.number}</h3>
            <Field label="题干文字">
              <Textarea
                value={q.content
                  .filter((c): c is Extract<Content, { kind: 'text' }> => c.kind === 'text')
                  .map((c) => c.text)
                  .join('\n')}
                onChange={(_, v) =>
                  patch(q.draft_key, {
                    content: [
                      { kind: 'text', text: v.value },
                      ...q.content.filter((c) => c.kind !== 'text'),
                    ],
                  })
                }
              />
            </Field>
            <Field label="标准答案">
              <Textarea
                value={q.standard_answer}
                onChange={(_, v) => patch(q.draft_key, { standard_answer: v.value })}
              />
            </Field>
            <Field label="解析">
              <Textarea
                value={q.explanation}
                onChange={(_, v) => patch(q.draft_key, { explanation: v.value })}
              />
            </Field>
            <Field label="满分（未知留空）">
              <Input
                type="number"
                min={0}
                value={q.points?.toString() ?? ''}
                onChange={(_, v) =>
                  patch(q.draft_key, {
                    points: v.value === '' ? null : Number(v.value),
                    ...(q.attempt
                      ? {
                          attempt: {
                            ...q.attempt,
                            grade: {
                              ...q.attempt.grade,
                              possible: v.value === '' ? null : Number(v.value),
                            },
                          },
                        }
                      : {}),
                  })
                }
              />
            </Field>
            {q.attempt && (
              <>
                <Field label="学生作答">
                  <Textarea
                    value={q.attempt.student_answer}
                    onChange={(_, v) =>
                      patch(q.draft_key, { attempt: { ...q.attempt!, student_answer: v.value } })
                    }
                  />
                </Field>
                <Field label="批改结果">
                  <Select
                    value={q.attempt.grade.verdict}
                    onChange={(_, v) =>
                      patch(q.draft_key, {
                        attempt: {
                          ...q.attempt!,
                          grade: { ...q.attempt!.grade, verdict: v.value as Verdict },
                        },
                      })
                    }
                  >
                    {Object.entries(verdicts).map(([v, text]) => (
                      <option key={v} value={v}>
                        {text}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="得分（无法判定留空）">
                  <Input
                    type="number"
                    min={0}
                    value={q.attempt.grade.earned?.toString() ?? ''}
                    onChange={(_, v) =>
                      patch(q.draft_key, {
                        attempt: {
                          ...q.attempt!,
                          grade: {
                            ...q.attempt!.grade,
                            earned: v.value === '' ? null : Number(v.value),
                          },
                        },
                      })
                    }
                  />
                </Field>
                <Field label="评分说明">
                  <Textarea
                    value={q.attempt.grade.reason}
                    onChange={(_, v) =>
                      patch(q.draft_key, {
                        attempt: { ...q.attempt!, grade: { ...q.attempt!.grade, reason: v.value } },
                      })
                    }
                  />
                </Field>
              </>
            )}
          </div>
        ))
      )}
      <div className="grading-actions">
        <Button busy={busy} onClick={() => void save()}>
          保存修改
        </Button>
        <Button variant="secondary" disabled={busy} onClick={onClose}>
          取消
        </Button>
      </div>
    </Modal>
  );
}

export function QuestionLibrary({ wrong }: { wrong: boolean }) {
  const client = useQueryClient();
  const questions = useQuery({
    queryKey: ['questions'],
    queryFn: api.listQuestions,
    enabled: desktopAvailable,
    refetchInterval: 5000,
  });
  const items =
    questions.data?.filter(
      (item) =>
        !wrong ||
        (item.question.attempt &&
          ['incorrect', 'partial', 'unanswered'].includes(item.question.attempt.grade.verdict)),
    ) ?? [];
  return (
    <div className="page">
      <div className="page-heading">
        <div>
          <h1>{wrong ? '错题整理' : '我的题库'}</h1>
          <p>
            {items.length} 道已保存题目{wrong ? ' · 错误、部分正确及未作答' : ''}
          </p>
        </div>
        <Link to="/import">
          <Button variant="secondary">导入材料</Button>
        </Link>
      </div>
      {questions.error && <Notice kind="error">{questions.error.message}</Notice>}
      {!items.length && (
        <EmptyState title="暂无题目">
          完成整卷识别后会自动入库。无法判定的题默认不进入错题视图。
        </EmptyState>
      )}
      {items.map((item) => (
        <div key={item.id}>
          <Link className="text-link" to={`/documents/${item.document_id}`}>
            打开原材料与来源对照 <ArrowSyncRegular />
          </Link>
          <QuestionCard
            saved={item}
            onSaved={() => client.invalidateQueries({ queryKey: ['questions'] })}
          />
        </div>
      ))}
    </div>
  );
}
