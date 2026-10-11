import {
  Activity,
  BookOpen,
  Bot,
  CalendarCheck,
  Bell,
  Building2,
  Code2,
  FlaskConical,
  FlaskRound,
  FileText,
  Link2,
  ListChecks,
  MessageSquare,
  Mic,
  PhoneCall,
  Plug,
  Radio,
  Receipt,
  ScanEye,
  Settings,
  ShieldCheck,
  Sparkles,
  Tag,
  TrendingUp,
  UserCog,
  UserRoundCog,
  Users,
  UsersRound,
  Workflow,
  Zap,
  type LucideIcon,
} from 'lucide-react'
import { DEFAULT_ROLE_CAPS, type AuthUser } from '@/lib/auth'
import type { MessageKey } from '@/i18n'

export interface NavDef {
  href: string
  /** Translation key of the label — render with `t(n.lbl)`. */
  lbl: MessageKey
  Icon: LucideIcon
  /** Extra path prefixes that also mark this item active (detail routes). */
  alsoActive?: string[]
  /** Capability that unlocks this item — the preferred gate. */
  /** Capability gate; an array means ANY of them. */
  cap?: string | string[]
  /** Role fallback when the session carries no capability list (an older
   *  cached session, or a backend that predates the matrix). */
  roles?: string[]
  /** Live counter shown beside the label (wired up by SideNav). */
  counter?: 'notifications' | 'waiting'
  /** Extra words the command palette matches on. */
  keywords?: string
}

export interface NavGroup {
  /** Translation key; undefined = the ungrouped head of the list (Dashboard, Notifications). */
  label?: MessageKey
  items: NavDef[]
}

/**
 * The information architecture. Every module the operator works in is one
 * entry here; pages built by other parts of the app (Tags, Developers, AI
 * monitor…) are listed even before they exist so the sidebar is the single
 * source of truth for navigation — the command palette searches the same list.
 */
export const NAV_GROUPS: NavGroup[] = [
  {
    items: [
      { href: '/dashboard', lbl: 'shell.nav.dashboard', Icon: TrendingUp, cap: 'analytics.view', keywords: 'home dashboard overview reports' },
      { href: '/notifications', lbl: 'shell.nav.notifications', Icon: Bell, counter: 'notifications', keywords: 'alerts bell inbox' },
    ],
  },
  {
    label: 'shell.groups.work',
    items: [
      { href: '/chats', lbl: 'shell.nav.chats', Icon: MessageSquare, cap: 'chats.view', counter: 'waiting', keywords: 'inbox conversations messages whatsapp email' },
      { href: '/contacts', lbl: 'shell.nav.contacts', Icon: Users, alsoActive: ['/contacts/'], cap: 'contacts.view', keywords: 'customers people crm' },
      { href: '/bookings', lbl: 'shell.nav.bookings', Icon: CalendarCheck, cap: 'chats.view', keywords: 'appointments calendar booking schedule' },
      { href: '/assignments', lbl: 'shell.nav.assignments', Icon: ListChecks, cap: 'account_assignment.manage', keywords: 'routing sticky numbers' },
    ],
  },
  {
    label: 'shell.groups.automate',
    items: [
      { href: '/journeys', lbl: 'shell.nav.flowBuilder', Icon: Workflow, alsoActive: ['/flow-builder'], cap: ['flows.view', 'flows.manage'], keywords: 'journeys flow flows flow builder workflow workflows campaigns automation perjalanan aliran 客户旅程 流程' },
      { href: '/ai-monitor', lbl: 'shell.nav.aiMonitor', Icon: ScanEye, cap: 'ai.monitor', keywords: 'drafts approve review bot' },
      { href: '/ai-playground', lbl: 'shell.nav.aiPlayground', Icon: FlaskRound, cap: ['ai.monitor', 'ai.behaviour.manage'], keywords: 'playground test ai try simulate sandbox prompt persona model compare ruang uji cuba simulasi 试验场 测试 试用 模拟 提示词' },
      { href: '/ai-tasks', lbl: 'shell.nav.aiTasks', Icon: Bot, cap: ['ai.monitor', 'settings.ai.manage'], keywords: 'background ai tasks invoice receipt extract silent worker sheets drive mcp tugas latar belakang invois resit 后台 任务 发票 收据' },
      { href: '/automations', lbl: 'shell.nav.automations', Icon: Zap, cap: 'flows.manage', keywords: 'rules triggers' },
      { href: '/persona', lbl: 'shell.nav.voice', Icon: Mic, cap: 'settings.ai.manage', keywords: 'voice persona calling' },
    ],
  },
  {
    label: 'shell.groups.library',
    items: [
      { href: '/templates', lbl: 'shell.nav.templates', Icon: FileText, alsoActive: ['/script-templates'], cap: ['templates.manage', 'script_templates.manage'], keywords: 'canned replies scripts messages' },
      { href: '/knowledge', lbl: 'shell.nav.knowledge', Icon: BookOpen, cap: 'settings.ai.manage', keywords: 'faq policies facts' },
      { href: '/skills', lbl: 'shell.nav.skills', Icon: Sparkles, cap: 'settings.ai.manage', keywords: 'instructions' },
      { href: '/ai-persona', lbl: 'shell.nav.aiPersona', Icon: UserRoundCog, cap: 'settings.ai.manage', keywords: 'persona house rules tone voice brand prompt peraturan nada 人设 规则 语气' },
      { href: '/tags', lbl: 'shell.nav.tags', Icon: Tag, cap: 'tags.view', keywords: 'labels fields' },
    ],
  },
  {
    label: 'shell.groups.channels',
    items: [
      { href: '/channels', lbl: 'shell.nav.channels', Icon: Radio, cap: 'channels.view', keywords: 'whatsapp telegram email accounts numbers widget' },
      { href: '/integrations', lbl: 'shell.nav.integrations', Icon: Plug, cap: 'integrations.view', keywords: 'google calendar stripe shopify' },
    ],
  },
  {
    label: 'shell.groups.admin',
    items: [
      { href: '/users', lbl: 'shell.nav.users', Icon: UserCog, cap: 'users.view', keywords: 'people accounts staff' },
      { href: '/teams', lbl: 'shell.nav.teams', Icon: UsersRound, cap: 'users.view' },
      { href: '/entities', lbl: 'shell.nav.entities', Icon: Building2, cap: 'entities.view', keywords: 'branches outlets' },
      { href: '/bindings', lbl: 'shell.nav.bindings', Icon: Link2, cap: 'users.view', keywords: 'access' },
      { href: '/permissions', lbl: 'shell.nav.permissions', Icon: ShieldCheck, cap: 'rbac.manage', keywords: 'rbac capabilities matrix' },
      { href: '/developers', lbl: 'shell.nav.developers', Icon: Code2, cap: 'api_keys.manage', keywords: 'developers tokens mcp' },
    ],
  },
  {
    label: 'shell.groups.review',
    items: [
      { href: '/billing', lbl: 'shell.nav.aiBill', Icon: Receipt, cap: 'billing.view', keywords: 'bill invoice usage cost ai tokens bil invois kos 账单 费用 用量' },
      { href: '/activity', lbl: 'shell.nav.activity', Icon: Activity, cap: 'activity.view', keywords: 'audit log' },
      { href: '/call-logs', lbl: 'shell.nav.callLogs', Icon: PhoneCall, alsoActive: ['/call-logs/'], cap: 'call_logs.view', keywords: 'calls recordings' },
      { href: '/test-lab', lbl: 'shell.nav.testLab', Icon: FlaskConical, cap: 'testlab.run', keywords: 'tests scenarios use cases qa demo data' },
    ],
  },
]

/** Footer link. Visible to everyone: per-user tabs (appearance…) live there,
 *  and the settings shell gates its own admin tabs. */
export const SETTINGS_NAV: NavDef = {
  href: '/settings',
  lbl: 'shell.nav.settings',
  Icon: Settings,
  alsoActive: ['/settings/'],
  keywords: 'preferences appearance theme configuration account profile password',
}

/** Where "home" is: the post-login landing page and the phone tab bar's Home. */
export const HOME = '/dashboard'

/**
 * Capability gate: `cap` when the session carries a resolved capability list;
 * otherwise the `roles` fallback, and failing that the role's default
 * capability set (so an item with only a `cap` still gates sensibly).
 */
export function canSee(user: AuthUser | null, n: NavDef): boolean {
  if (!n.cap && !n.roles) return true
  const role = user?.role ?? 'agent'
  const caps = user?.capabilities
  const need = n.cap ? ([] as string[]).concat(n.cap) : []
  if (Array.isArray(caps) && caps.length > 0 && need.length) return need.some((c) => caps.includes(c))
  if (n.roles) return n.roles.includes(role)
  return need.length ? need.some((c) => (DEFAULT_ROLE_CAPS[role] ?? []).includes(c)) : true
}

export function isActive(pathname: string, n: NavDef): boolean {
  return pathname === n.href || (n.alsoActive?.some((p) => pathname.startsWith(p)) ?? false)
}

/** The groups this user can see, empty groups dropped. */
export function visibleGroups(user: AuthUser | null): NavGroup[] {
  return NAV_GROUPS.map((g) => ({ ...g, items: g.items.filter((n) => canSee(user, n)) })).filter(
    (g) => g.items.length > 0,
  )
}

/**
 * The sidebar entry a route belongs to — its own href, anything beneath it
 * (detail routes) or an `alsoActive` alias — or null for an ungated route
 * (Settings gates its own pages, Notifications and Help are everyone's).
 * The shell's route guard checks it with `canSee` before a page mounts.
 */
export function routeDef(pathname: string): NavDef | null {
  const under = (base: string) =>
    base.endsWith('/') ? pathname.startsWith(base) : pathname === base || pathname.startsWith(base + '/')
  for (const g of NAV_GROUPS)
    for (const n of g.items) if (under(n.href) || n.alsoActive?.some(under)) return n
  return null
}

/** Where to send someone who can't open the page they asked for. */
export function firstVisibleHref(user: AuthUser | null): string {
  return visibleGroups(user)[0]?.items[0]?.href ?? '/settings/appearance'
}

/** Flat list for the command palette's "Pages" group. */
export function visiblePages(user: AuthUser | null): (NavDef & { group?: MessageKey })[] {
  return [
    ...visibleGroups(user).flatMap((g) => g.items.map((n) => ({ ...n, group: g.label }))),
    SETTINGS_NAV,
  ]
}
