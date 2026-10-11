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
    invalid: '用户名与密码不匹配。',
    locked_one: '失败次数过多，请 {count} 分钟后再试。',
    locked_other: '失败次数过多，请 {count} 分钟后再试。',
    rateLimited: '此网络的登录尝试过于频繁，请稍候再试。',
  },
  twoFactor: {
    eyebrow: '两步登录',
    title: '输入验证码',
    subtitle: '打开你的身份验证器应用，输入它为 {issuer} 显示的 6 位验证码。',
    code: '应用中的验证码',
    recovery: '恢复码',
    verify: '验证',
    useRecovery: '手机丢了？使用恢复码',
    useApp: '改用应用中的验证码',
    invalid: '验证码不正确。请确认手机时间准确，并输入最新的验证码。',
    expired: '本次登录已过期——请重新输入密码。',
    back: '返回登录',
  },
  setup: {
    eyebrow: '账号设置',
    passwordTitle: '设置你自己的密码',
    passwordIntro: '此账号仍在使用安装时的初始密码，读过安装指南的人都知道它。请先设置你自己的密码再继续。',
    twoFactorTitle: '设置两步登录',
    twoFactorIntro: '你的组织要求每次登录时输入身份验证器应用中的验证码。现在就设置吧——只需一分钟。',
    signOut: '退出登录',
    continue: '进入 ChatBranch',
  },
}
