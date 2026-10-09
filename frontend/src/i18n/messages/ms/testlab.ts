import type { testlab as source } from '../en/testlab'
import type { Dict } from '../../types'

export const testlab: Dict<typeof source> = {
  title: 'Makmal ujian',
  eyebrow: 'Semakan',
  subtitle:
    'Kes penggunaan berskrip yang berjalan dari awal hingga akhir melalui saluran paip sebenar — di dalam entiti “Test lab” yang berpagar dan tidak menyentuh data anda sendiri.',
  off: {
    title: 'Makmal ujian dimatikan',
    body: 'Pelayan ini berjalan dengan TEST_LAB=0, jadi makmal ujian tidak tersedia di sini.',
  },
  status: {
    none: 'Belum ada data ujian — entiti “Test lab” dicipta kali pertama senario dijalankan.',
    data: '{contacts} kenalan dan {conversations} perbualan dalam “{entity}”.',
    demo: 'Data demo dimuatkan',
    aiReal: 'Kunci AI dikonfigurasi: senario bertanda “Kunci AI” turut menyemak jawapan model sebenar.',
    aiSandbox:
      'Tiada kunci AI: otak ujian deterministik makmal menulis kata-kata AI; senario bertanda “Kunci AI” menjalankan varian deterministiknya.',
    cli: 'Dari terminal: npm run test:scenarios',
  },
  actions: {
    runSelected: 'Jalankan yang dipilih ({count})',
    runAll: 'Jalankan semua',
    stop: 'Berhenti selepas yang ini',
    loadDemo: 'Muatkan data demo',
    reset: 'Set semula data ujian',
    selectCategory: 'Pilih semua dalam {category}',
    details: 'Butiran',
    hideDetails: 'Sembunyikan butiran',
    runOne: 'Jalankan',
  },
  progress: {
    running: 'Menjalankan {done} daripada {total} — {title}',
    stopping: 'Berhenti selepas senario semasa…',
    done: 'Selesai: {passed} lulus, {failed} gagal, {skipped} dilangkau.',
  },
  result: {
    passed: 'Lulus',
    failed: 'Gagal',
    skipped: 'Dilangkau',
    running: 'Sedang berjalan…',
    queued: 'Dalam giliran',
    notRun: 'Belum dijalankan',
    seconds: '{seconds} s',
    brainLab: 'Otak ujian',
    brainReal: 'Model AI sebenar',
    checks: '{ok} daripada {total} semakan',
  },
  badge: {
    aiKey: 'Kunci AI',
  },
  detail: {
    steps: 'Langkah',
    expect: 'Dijangka',
    checks: 'Semakan',
    links: 'Buka apa yang dihasilkan',
    error: 'Ralat',
    skipped: 'Dilangkau: {reason}',
  },
  history: {
    title: 'Sejarah larian',
    subtitle: '50 larian terakhir, dari halaman ini, CLI dan suite ujian.',
    empty: 'Belum ada larian.',
    summary: '{passed} lulus · {failed} gagal · {skipped} dilangkau',
    by: 'oleh {user}',
    show: 'Tunjukkan keputusan',
    showing: 'Menunjukkan larian #{id} dari {when}.',
    sources: {
      page: 'Halaman',
      cli: 'CLI',
      api: 'API',
      test: 'Suite ujian',
    },
  },
  demo: {
    loaded: 'Data demo dimuatkan: {contacts} kenalan, {conversations} perbualan, {bookings} tempahan.',
    already: 'Data demo sudah dimuatkan — set semula data ujian untuk memuatkannya semula.',
  },
  reset: {
    title: 'Set semula data ujian?',
    body: 'Ini memadam semua di dalam entiti “Test lab”: perbualan, kenalan, draf AI, tempahan, mesej dalam giliran, larian aliran, peti masuk, pasukan, tetapan dan pengguna testlab_*. Tiada apa-apa di luarnya disentuh, dan sejarah larian disimpan.',
    confirm: 'Padam data ujian',
    done: 'Data ujian dipadam ({count} baris).',
  },
  errors: {
    load: 'Makmal ujian tidak dapat dimuatkan — {error}',
    action: 'Itu tidak berjaya — {error}',
  },
}
