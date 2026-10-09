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
  },
}
