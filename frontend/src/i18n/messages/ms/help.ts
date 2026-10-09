import type { help as source } from '../en/help'
import type { Dict } from '../../types'

export const help: Dict<typeof source> = {
  title: 'Pusat Bantuan',
  subtitle: 'Jawapan kepada soalan yang paling kerap kami terima.',
  search: 'Cari artikel bantuan',
  searchPlaceholder: 'Cari artikel bantuan…',
  unavailable: 'Pusat bantuan tidak tersedia buat sementara waktu. Sila cuba lagi sebentar lagi.',
  noMatch: 'Tiada yang sepadan dengan “{q}”.',
  empty: 'Belum ada artikel yang diterbitkan.',
  allArticles: 'Semua artikel',
  missing: 'Artikel itu tidak wujud (atau belum diterbitkan).',
  related: 'Berkaitan',
}
