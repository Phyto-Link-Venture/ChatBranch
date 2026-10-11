import type { auth as source } from '../en/auth'
import type { Dict } from '../../types'

export const auth: Dict<typeof source> = {
  login: {
    eyebrow: 'Ruang kerja',
    title: 'Selamat kembali',
    subtitle: 'Log masuk untuk menyambung perbualan anda.',
    username: 'Nama pengguna',
    password: 'Kata laluan',
    showPassword: 'Tunjukkan kata laluan',
    hidePassword: 'Sembunyikan kata laluan',
    expired: 'Sesi anda telah tamat — sila log masuk semula.',
    signIn: 'Log masuk',
    provisioned: 'Akaun disediakan oleh pentadbir anda — hubungi mereka jika anda tidak dapat log masuk.',
    tagline: 'Satu peti masuk untuk WhatsApp, e-mel, media sosial, sembang web dan suara.',
    invalid: 'Nama pengguna dan kata laluan itu tidak sepadan.',
    locked_one: 'Terlalu banyak cubaan gagal. Cuba lagi dalam {count} minit.',
    locked_other: 'Terlalu banyak cubaan gagal. Cuba lagi dalam {count} minit.',
    rateLimited: 'Terlalu banyak cubaan log masuk dari rangkaian ini. Tunggu sebentar dan cuba lagi.',
  },
  twoFactor: {
    eyebrow: 'Log masuk dua langkah',
    title: 'Masukkan kod anda',
    subtitle: 'Buka aplikasi pengesah anda dan masukkan kod 6 digit yang ditunjukkan untuk {issuer}.',
    code: 'Kod daripada aplikasi',
    recovery: 'Kod pemulihan',
    verify: 'Sahkan',
    useRecovery: 'Telefon hilang? Gunakan kod pemulihan',
    useApp: 'Gunakan kod daripada aplikasi',
    invalid: 'Kod itu tidak betul. Pastikan jam telefon anda tepat dan cuba kod yang terbaharu.',
    expired: 'Log masuk ini telah tamat — masukkan kata laluan anda semula.',
    back: 'Kembali ke log masuk',
  },
  setup: {
    eyebrow: 'Persediaan akaun',
    passwordTitle: 'Pilih kata laluan anda sendiri',
    passwordIntro:
      'Akaun ini masih menggunakan kata laluan semasa pemasangan, yang diketahui oleh sesiapa yang membaca panduan pemasangan. Pilih kata laluan anda sendiri sebelum meneruskan.',
    twoFactorTitle: 'Sediakan log masuk dua langkah',
    twoFactorIntro:
      'Organisasi anda memerlukan kod daripada aplikasi pengesah setiap kali log masuk. Sediakannya sekarang — ia mengambil masa seminit.',
    signOut: 'Log keluar',
    continue: 'Teruskan ke ChatBranch',
  },
}
