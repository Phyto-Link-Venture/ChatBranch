import type { manual as source } from '../../en/manual'
import type { Dict } from '../../../types'
import { start } from './start'
import { channels } from './channels'
import { work } from './work'
import { ai } from './ai'
import { automate } from './automate'
import { admin } from './admin'
import { faq, glossary } from './help'

export const manual: Dict<typeof source> = {
  ui: {
    searchPlaceholder: '搜索手册 — 例如“WhatsApp”、“关闭 AI”',
    noResults: '手册中没有与“{q}”匹配的内容。',
    sections: {
      quickStart: '第一天',
      guides: '分步指南',
      tours: '每个页面导览',
      faq: '遇到问题时',
      glossary: '常用词汇',
    },
    quickStartIntro: '刚开始使用 ChatBranch？按顺序完成以下事项——从空账户到能回复客户的 AI，大约一小时。',
    guidesIntro: '每篇指南带你从头到尾完成一项工作。照着编号步骤做即可，无需任何技术知识。',
    toursIntro: '每个页面显示什么、各部分有什么用。在 ChatBranch 中，按顶栏的 ? 按钮即可在真实页面上查看。',
    faqIntro: '常见问题以及该检查什么。',
    glossaryIntro: '用浅白语言解释 ChatBranch 的词汇。',
    groups: {
      start: '入门',
      channels: '连接你的渠道',
      work: '日常工作',
      ai: '设置 AI',
      automate: '自动化',
      admin: '团队与管理',
    },
    beforeYouStart: '开始之前',
    steps: '步骤',
    done: '如何确认成功',
    tips: '小贴士',
    trouble: '遇到问题时',
    openPage: '打开此页面',
    showMe: '在页面上演示',
    backToTop: '回到顶部',
    minutes_one: '约 {count} 分钟',
    minutes_other: '约 {count} 分钟',
    who: {
      everyone: '团队中任何人',
      admin: '管理员',
      superadmin: '所有者（超级管理员）',
    },
    forThisPage: '本页面的操作指南',
    allGuides: '所有分步指南',
  },
  guides: {
    ...start,
    ...channels,
    ...work,
    ...ai,
    ...automate,
    ...admin,
  },
  faq,
  glossary,
}
