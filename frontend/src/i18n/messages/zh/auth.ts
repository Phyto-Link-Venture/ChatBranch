import type { auth as source } from '../en/auth'
import type { Dict } from '../../types'

export const auth: Dict<typeof source> = {
  login: {
    eyebrow: '工作区',
    title: '欢迎回来',
    subtitle: '登录后继续处理你的对话。',
    username: '用户名',
    password: '密码',
    showPassword: '显示密码',
    hidePassword: '隐藏密码',
    expired: '你的会话已过期——请重新登录。',
    signIn: '登录',
    provisioned: '账号由管理员开通——如无法登录，请联系管理员。',
    tagline: '一个收件箱，统一处理 WhatsApp、邮件、社交媒体、网页聊天和语音。',
  },
}
