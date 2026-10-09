import type { help as source } from '../en/help'
import type { Dict } from '../../types'

export const help: Dict<typeof source> = {
  title: '帮助中心',
  subtitle: '常见问题解答。',
  search: '搜索帮助文章',
  searchPlaceholder: '搜索帮助文章…',
  unavailable: '帮助中心暂时不可用，请稍后再试。',
  noMatch: '没有与“{q}”匹配的内容。',
  empty: '还没有已发布的文章。',
  allArticles: '全部文章',
  missing: '该文章不存在（或尚未发布）。',
  related: '相关文章',
}
