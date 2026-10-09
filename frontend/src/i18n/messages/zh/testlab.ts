import type { testlab as source } from '../en/testlab'
import type { Dict } from '../../types'

export const testlab: Dict<typeof source> = {
  title: '测试实验室',
  eyebrow: '审查',
  subtitle: '通过真实流程端到端运行的脚本化用例——全部在隔离的“Test lab”实体内，绝不触碰您自己的数据。',
  off: {
    title: '测试实验室已关闭',
    body: '此服务器以 TEST_LAB=0 运行，因此这里无法使用测试实验室。',
  },
  status: {
    none: '暂无测试数据——首次运行场景时会创建“Test lab”实体。',
    data: '“{entity}”中有 {contacts} 个联系人和 {conversations} 个对话。',
    demo: '已加载演示数据',
    aiReal: '已配置 AI 密钥：标有“AI 密钥”的场景还会检查真实模型的回答。',
    aiSandbox: '未配置 AI 密钥：由实验室的确定性测试大脑撰写 AI 的回复；标有“AI 密钥”的场景运行其确定性版本。',
    cli: '在终端中：npm run test:scenarios',
  },
  actions: {
    runSelected: '运行所选（{count}）',
    runAll: '全部运行',
    stop: '运行完当前场景后停止',
    loadDemo: '加载演示数据',
    reset: '重置测试数据',
    selectCategory: '全选“{category}”',
    details: '详情',
    hideDetails: '隐藏详情',
    runOne: '运行',
  },
  progress: {
    running: '正在运行第 {done} / {total} 个——{title}',
    stopping: '当前场景结束后停止…',
    done: '完成：{passed} 个通过，{failed} 个失败，{skipped} 个跳过。',
  },
  result: {
    passed: '通过',
    failed: '失败',
    skipped: '已跳过',
    running: '运行中…',
    queued: '排队中',
    notRun: '未运行',
    seconds: '{seconds} 秒',
    brainLab: '测试大脑',
    brainReal: '真实 AI 模型',
    checks: '{total} 项检查中通过 {ok} 项',
  },
  badge: {
    aiKey: 'AI 密钥',
  },
  detail: {
    steps: '步骤',
    expect: '预期结果',
    checks: '检查',
    links: '打开生成的内容',
    error: '错误',
    skipped: '已跳过：{reason}',
  },
  history: {
    title: '运行记录',
    subtitle: '最近 50 次运行，来自此页面、命令行和测试套件。',
    empty: '暂无运行记录。',
    summary: '{passed} 通过 · {failed} 失败 · {skipped} 跳过',
    by: '由 {user} 运行',
    show: '查看结果',
    showing: '正在显示 {when} 的第 #{id} 次运行。',
    sources: {
      page: '页面',
      cli: '命令行',
      api: 'API',
      test: '测试套件',
    },
  },
  demo: {
    loaded: '已加载演示数据：{contacts} 个联系人、{conversations} 个对话、{bookings} 个预约。',
    already: '演示数据已加载——重置测试数据后可重新加载。',
  },
  reset: {
    title: '重置测试数据？',
    body: '这将删除“Test lab”实体内的所有内容：其对话、联系人、AI 草稿、预约、排队消息、流程运行、收件箱、团队、设置以及 testlab_* 用户。实体之外的任何内容都不会受影响，运行记录也会保留。',
    confirm: '删除测试数据',
    done: '测试数据已删除（{count} 行）。',
  },
  errors: {
    load: '无法加载测试实验室——{error}',
    action: '操作未成功——{error}',
  },
}
