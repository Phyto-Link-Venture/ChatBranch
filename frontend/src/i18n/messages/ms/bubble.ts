import type { bubble as source } from '../en/bubble'
import type { Dict } from '../../types'

export const bubble: Dict<typeof source> = {
  actions: {
    aria: 'Tindakan mesej',
    reply: 'Balas',
    copy: 'Salin teks',
    copied: 'Disalin',
    react: 'Beri reaksi',
    moreEmoji: 'Lagi emoji',
    close: 'Tutup',
  },
  reactions: {
    you: 'Anda',
    customer: 'Pelanggan',
    by: '{names} memberi reaksi {emoji}',
    removeYours: 'Klik untuk membuang reaksi anda',
    addSame: 'Klik untuk memberi reaksi {emoji}',
  },
  quote: {
    you: 'Anda',
    ai: 'AI',
    customer: 'Pelanggan',
    missing: 'Mesej terdahulu',
    replyingTo: 'Membalas {who}',
    cancel: 'Batal balasan',
    jump: 'Tunjukkan mesej asal',
    media: {
      image: 'Foto',
      video: 'Video',
      audio: 'Audio',
      document: 'Dokumen',
    },
  },
  location: {
    title: 'Lokasi dikongsi',
    open: 'Buka dalam Peta',
  },
  contactCard: {
    title: 'Kad kenalan',
    add: 'Tambah sebagai kenalan',
    adding: 'Menambah…',
    added: 'Kenalan ditambah',
    open: 'Buka kenalan',
  },
  sticker: 'Pelekat',
  emoji: {
    open: 'Emoji',
    search: 'Cari emoji',
    recent: 'Baru digunakan',
    noResults: 'Tiada emoji ditemui',
    close: 'Tutup',
    categories: {
      smileys: 'Senyuman',
      people: 'Orang & tangan',
      hearts: 'Hati & simbol',
      nature: 'Haiwan & alam',
      food: 'Makanan & minuman',
      activities: 'Aktiviti & perjalanan',
      objects: 'Objek',
    },
  },
}
