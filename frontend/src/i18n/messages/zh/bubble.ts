import type { bubble as source } from '../en/bubble'
import type { Dict } from '../../types'

export const bubble: Dict<typeof source> = {
  actions: {
    aria: '消息操作',
    reply: '回复',
    copy: '复制文字',
    copied: '已复制',
    react: '表情回应',
    moreEmoji: '更多表情',
    close: '关闭',
  },
  reactions: {
    you: '你',
    customer: '客户',
    ai: 'AI',
    by: '{names} 回应了 {emoji}',
    removeYours: '点击移除你的回应',
    addSame: '点击回应 {emoji}',
  },
  quote: {
    you: '你',
    ai: 'AI',
    customer: '客户',
    missing: '一条较早的消息',
    replyingTo: '回复 {who}',
    cancel: '取消回复',
    jump: '显示原消息',
    media: {
      image: '照片',
      video: '视频',
      audio: '音频',
      document: '文件',
    },
  },
  location: {
    title: '共享位置',
    open: '在地图中打开',
  },
  contactCard: {
    title: '联系人名片',
    add: '添加为联系人',
    adding: '添加中…',
    added: '已添加联系人',
    open: '打开联系人',
  },
  sticker: '贴纸',
  transcript: '转写文本',
  emoji: {
    open: '表情',
    search: '搜索表情',
    recent: '最近使用',
    noResults: '没有找到表情',
    close: '关闭',
    categories: {
      smileys: '笑脸',
      people: '人物与手势',
      hearts: '爱心与符号',
      nature: '动物与自然',
      food: '食物与饮料',
      activities: '活动与旅行',
      objects: '物品',
    },
  },
}
