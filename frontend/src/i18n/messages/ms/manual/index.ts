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
    searchPlaceholder: 'Cari dalam manual — cth. “WhatsApp”, “matikan AI”',
    noResults: 'Tiada apa-apa dalam manual yang sepadan dengan “{q}”.',
    sections: {
      quickStart: 'Hari pertama anda',
      guides: 'Panduan langkah demi langkah',
      tours: 'Lawatan setiap halaman',
      faq: 'Jika ada masalah',
      glossary: 'Istilah yang kami gunakan',
    },
    quickStartIntro: 'Baru menggunakan ChatBranch? Buat ini mengikut turutan — kira-kira sejam daripada akaun kosong hingga AI yang menjawab pelanggan anda.',
    guidesIntro: 'Setiap panduan membawa anda melalui satu tugasan dari mula hingga akhir. Ikut langkah bernombor dengan tepat; tiada pengetahuan teknikal diperlukan.',
    toursIntro: 'Apa yang ditunjukkan oleh setiap halaman dan fungsi setiap bahagian. Dalam ChatBranch, tekan butang ? di bar atas untuk melihatnya pada halaman sebenar.',
    faqIntro: 'Masalah biasa dan apa yang perlu disemak.',
    glossaryIntro: 'Istilah ChatBranch dalam bahasa mudah.',
    groups: {
      start: 'Bermula',
      channels: 'Sambungkan saluran anda',
      work: 'Kerja harian',
      ai: 'Sediakan AI',
      automate: 'Automasi',
      admin: 'Pasukan & pentadbiran',
    },
    beforeYouStart: 'Sebelum anda mula',
    steps: 'Langkah',
    done: 'Bagaimana anda tahu ia berjaya',
    tips: 'Petua',
    trouble: 'Jika ada masalah',
    openPage: 'Buka halaman ini',
    showMe: 'Tunjukkan pada halaman',
    backToTop: 'Kembali ke atas',
    minutes_one: 'Kira-kira {count} minit',
    minutes_other: 'Kira-kira {count} minit',
    who: {
      everyone: 'Sesiapa dalam pasukan',
      admin: 'Pentadbir',
      superadmin: 'Pemilik (superadmin)',
    },
    forThisPage: 'Panduan cara untuk halaman ini',
    allGuides: 'Semua panduan langkah demi langkah',
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
