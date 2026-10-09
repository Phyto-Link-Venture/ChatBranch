import type { ui as source } from '../en/ui'
import type { Dict } from '../../types'

export const ui: Dict<typeof source> = {
  search: {
    placeholder: 'Cari…',
  },
  select: {
    placeholder: 'Pilih…',
    any: 'Mana-mana',
    selectShown: 'Pilih yang dipaparkan',
    searchOptions: 'Cari pilihan',
    noMatches: 'Tiada padanan',
  },
  dataTable: {
    noResultsFor: 'Tiada hasil untuk “{query}”.',
    range: '{from}–{to} daripada {total}',
    page: 'Halaman {page} / {pages}',
    previousPage: 'Halaman sebelumnya',
    nextPage: 'Halaman seterusnya',
  },
  bindingGrid: {
    bound: 'Dipautkan',
    allN: 'Semua {count}',
    kOfN: '{k} daripada {n}',
    checkAll: 'Tandakan semua',
    clearAll: 'Kosongkan semua',
    noMatchesFor: 'Tiada padanan untuk “{query}”.',
    empty: 'Belum ada apa-apa untuk dipautkan.',
    bindAll: 'Pautkan semua dalam {group}',
    manage: 'Urus',
    manageHint: 'Hidupkan atau matikan setiap satu, kemudian tekan Guna. Tiada apa-apa disimpan sehingga anda menekan Simpan pada halaman.',
    onCount: '{k} daripada {n} hidup',
    allOn: 'Hidupkan semua',
    allOff: 'Matikan semua',
  },
  saveBar: {
    unsaved: 'Perubahan belum disimpan',
    saveChanges: 'Simpan perubahan',
  },
  entityPicker: {
    orgWide: 'Seluruh organisasi (dikongsi oleh semua entiti)',
    choose: 'Pilih entiti…',
    allEntities: 'Semua entiti',
    main: 'Utama',
  },
  phone: {
    number: 'Nombor telefon',
    countryCode: 'Kod panggilan negara',
  },
  tagList: {
    removeItem: 'Alih keluar {item}',
  },
  contactStatus: {
    active: 'Aktif',
    pending: 'Menunggu',
    messageSent: 'Mesej dihantar',
    resolved: 'Selesai',
    failed: 'Gagal',
  },
  callStatus: {
    completed: 'Selesai',
    live: 'Langsung',
    voicemail: 'Mel suara',
    noAnswer: 'Tiada jawapan',
    callbackRequested: 'Minta panggil semula',
    read: 'Dibaca',
    delivered: 'Diterima',
  },
  health: {
    healthy: 'Sihat',
    degraded: 'Terjejas',
    unknown: 'Tidak diketahui',
    disconnected: 'Terputus',
    banned: 'Disekat',
  },
  accountRole: {
    reminder: 'Peringatan',
    reminderHint: 'Peringatan (perniagaan) — menghantar templat dan menyerahkan kenalan kepada nombor perbualan',
    conversation: 'Perbualan',
    conversationHint: 'Perbualan (peribadi) — sembang AI, dimulakan oleh pelanggan sahaja',
  },
  channel: {
    email: 'E-mel',
    website: 'Sembang laman web',
    voice: 'Suara',
    chat: 'Sembang',
  },
  languages: {
    auto: 'Kesan automatik',
  },
}
