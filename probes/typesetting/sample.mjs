// Original synthetic questions only; no imported student material.
const text = text => ({type: 'text', text});
const math = latex => ({type: 'math', latex});
export const sample = {
  title: '数学与化学排版验证卷',
  questions: [
    {id: 'Q1', section: '一、选择与填空（共 30 分）', body: [text('计算下列分式，并说明定义域：'), math('\\frac{x^2-1}{x-1}+\\frac{1}{\\sqrt{x+2}}')], options: ['A. 所有实数', 'B. 大于负二且不等于一', 'C. 不小于负二', 'D. 大于一']},
    {id: 'Q2', body: [text('给定矩阵，求它的行列式：'), math('A=\\begin{pmatrix}1&2&3\\\\0&-1&4\\\\2&0&1\\end{pmatrix}')], blankMm: 20},
    {id: 'Q3', body: [text('比较相邻公式与汉字的基线。函数为'), math('f(x)=x_1^2+e^{-x}+\\sum_{k=1}^{n}k^2'), text('，写出求导步骤。')], blankMm: 25},
    {id: 'Q4', body: [text('中文公式必须作为固定字体轮廓保存在 SVG 中：'), math('\\text{平均速度}=\\frac{\\text{总路程}}{\\text{总时间}}')], blankMm: 20},
    {id: 'Q5', section: '二、化学与图表（共 30 分）', body: [text('观察反应式，写出反应条件并配平：'), math('\\ce{2H2 + O2 ->[\\text{点燃}] 2H2O}'), text('；'), math('\\ce{SO4^2- + Ba^2+ -> BaSO4 v}')], blankMm: 20},
    {id: 'Q6', body: [text('根据实验数据选择正确说法。下表必须完整进入同一栏，不能压缩到不可读。')], table: [['选项', '温度 / ℃', '现象', '结论'], ['A', '20', '无明显变化', '速率较低'], ['B', '40', '有少量气泡', '速率提高'], ['C', '60', '大量气泡', '需要控制变量']], blankMm: 15},
    {id: 'Q7', body: [text('下图为自建结构图，标出碳原子位置并描述键的关系。')], figure: true, blankMm: 25},
    {id: 'Q8', section: '三、解答题（共 40 分）', body: [text('阅读材料并依次作答。每个小问可独立分页，续题保留题号；本题用于覆盖栏末图表、长小问和作答留白。')], parts: Array.from({length: 12}, (_,i) => ({label: `（${i+1}）`, body: [text(`第 ${i+1} 小问：在相同实验条件下记录自变量与因变量，分析误差来源，给出推理过程并解释结论。必须联系题干中的限制条件，说明结论成立的范围。`), ...(i % 3 === 0 ? [math('\\frac{a_{n+1}}{a_n}=\\frac{n+2}{n+1}')] : [])], blankMm: 17}))},
    {id: 'Q9', body: [text('数据输入边界验证（以下为普通文字）：#panic("UNTRUSTED_TYPST")。请原样阅读，不应执行。')], blankMm: 10},
  ].map((q,i) => ({...q, number: i+1,
    answer: `ANSWER_ONLY_${i+1}：` + ['选 B；原式化为 x + 1 加根式倒数。', '行列式为 21。', '将下标变量与 n 视作常量，导数为负的指数项。', '平均速度等于总路程除以总时间，时间须大于零。', '第一式点燃；第二式生成硫酸钡沉淀，两式系数均已配平。', '升温时气泡增多；比较速率时须保持浓度等其他条件相同。', '共有七个碳原子，六元环上为交替双键，侧链为单键。', '各小问按控制变量、数据证据和适用范围作答。', '题中文字原样显示，未执行其中的 Typst 代码。'][i],
    explanation: `逐项核验题号 ${i+1}，保留题干条件；本数据仅验证排版，不作为 AI 解题准确率样本。`,
    parts: q.parts?.map((p,j) => ({...p, answer: `SUBANSWER_${j+1}：保持其他变量相同，以重复实验数据比较变化趋势；说明仪器误差和结论范围。`})),
    studentAttempt: `STUDENT_PRIVATE_${i+1}：学生作答不得进入复习卷。`}))
};
