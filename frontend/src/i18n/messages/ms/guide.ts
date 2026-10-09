import type { guide as source } from '../en/guide'
import type { Dict } from '../../types'

export const guide: Dict<typeof source> = {
  ui: {
    button: 'Panduan',
    thisPage: 'Tunjukkan panduan halaman ini',
    appTour: 'Lawatan ChatBranch',
    manual: 'Buka manual pengguna',
    autoStart: 'Tunjukkan panduan pada lawatan pertama',
    stepOf: 'Langkah {n} daripada {count}',
    next: 'Seterusnya',
    back: 'Kembali',
    skip: 'Langkau',
    done: 'Selesai',
    close: 'Tutup panduan',
  },
  manual: {
    title: 'Manual pengguna ChatBranch',
    subtitle: 'Panduan praktikal untuk setiap bahagian ChatBranch — baca di sini, muat turun, atau letak di laman web anda sendiri.',
    contents: 'Kandungan',
    showMe: 'Tunjukkan dalam aplikasi',
    downloadPdf: 'Muat turun PDF',
    downloadHtml: 'Muat turun HTML',
    embed: 'Letak di laman web anda',
    embedHelp: 'Tampal ini di tempat anda mahu manual dipaparkan. Ia sentiasa dikemas kini apabila ChatBranch berubah.',
    copy: 'Salin',
    copied: 'Disalin',
    standaloneHelp: 'Mahu salinan sendiri? “Muat turun HTML” menyimpan satu fail yang boleh dihoskan di mana-mana.',
    groups: {
      start: 'Bermula',
      work: 'Kerja harian',
      ai: 'AI',
      setup: 'Persediaan & bil',
    },
    footer: 'Setiap bab juga merupakan lawatan berpandu dalam ChatBranch: buka halaman itu dan tekan butang ? di bar atas untuk mengulanginya.',
    generated: 'Dijana {date}',
  },
  tours: {
    shell: {
      title: 'Mengenali ChatBranch',
      intro: 'Bahagian ChatBranch yang anda gunakan di setiap halaman.',
      steps: {
        s1: {
          title: 'Bar sisi',
          body: 'Setiap bahagian ChatBranch ada di sini, dikumpulkan sebagai Kerja, Automasi, Pustaka, Saluran, Admin dan Semakan. Anda hanya melihat apa yang dibenarkan oleh peranan anda.',
        },
        s2: {
          title: 'Cari apa sahaja',
          body: 'Tekan Ctrl+K (⌘K pada Mac) untuk mencari kenalan, perbualan, templat, perjalanan dan halaman dari mana-mana.',
        },
        s3: {
          title: 'Pilih cawangan',
          body: 'Jika perniagaan anda mempunyai beberapa cawangan atau syarikat, pilih satu di sini — setiap halaman kemudian hanya menunjukkan data cawangan itu.',
        },
        s4: {
          title: 'Langsung sekarang',
          body: 'Berapa banyak chat sedang menunggu dan berapa panggilan sedang berlangsung. Titik menunjukkan sama ada peti masuk bersambung secara masa nyata.',
        },
        s5: {
          title: 'Tanya ChatBranch',
          body: 'Pembantu anda: tanya soalan atau beritahu apa yang perlu dibuat (“cari pesanan terakhir Siti”, “matikan AI untuk chat ini”). Ia bertanya dahulu sebelum mengubah apa-apa.',
        },
        s6: {
          title: 'Panduan',
          body: 'Ulang panduan untuk halaman ini, ambil lawatan ini semula, atau buka manual penuh.',
        },
      },
    },
    analytics: {
      title: 'Analitik — halaman utama anda',
      intro: 'Prestasi perbualan, panggilan dan AI.',
      steps: {
        s1: {
          title: 'Halaman utama anda',
          body: 'Analitik ialah perkara pertama yang anda lihat selepas log masuk: ringkasan tempoh yang anda pilih, untuk cawangan yang anda pilih.',
        },
        s2: {
          title: 'Pilih tempoh',
          body: 'Tukar antara 7, 30 dan 90 hari. Setiap kad di bawah akan mengikutinya.',
        },
        s3: {
          title: 'Angka utama',
          body: 'Mesej dihantar dan diterima, berapa pelanggan membalas, panggilan dan hasilnya — dengan perubahan berbanding tempoh sebelumnya.',
        },
        s4: {
          title: 'Lebih mendalam',
          body: 'Laporan memecahkan angka yang sama mengikut ejen, pasukan, label dan saluran. Kos AI dan bil syarikat ada di Semakan → Bil AI.',
        },
      },
    },
    chats: {
      title: 'Chat — peti masuk bersatu',
      intro: 'Setiap saluran dalam satu peti masuk: balas, serahkan, dan biarkan AI membantu.',
      steps: {
        s1: {
          title: 'Cari perbualan',
          body: 'Cari mengikut nama, nombor atau apa sahaja yang disebut dalam chat. Penapis membuka semua penapis: saluran, status, penerima tugasan, AI hidup atau mati, menunggu balasan dan lain-lain.',
        },
        s2: {
          title: 'Paparan',
          body: 'Semua, Saya, Belum ditugaskan dan Perlu manusia ialah paparan sedia ada. Simpan penapis anda sendiri sebagai tab baharu dengan menu •••.',
        },
        s3: {
          title: 'Senarai perbualan',
          body: 'Aktiviti terbaru dahulu. Masa berwarna oren bermaksud pelanggan sedang menunggu balasan; cip menunjukkan sama ada AI menjawab chat ini.',
        },
        s4: {
          title: 'AI hidup atau mati untuk chat ini',
          body: 'Matikan AI untuk menjawab sendiri, atau hidupkan semula supaya ia membalas. Jika peraturan di atas yang menentukan, suis dikunci dan menyatakan sebabnya.',
        },
        s5: {
          title: 'Balas atau tambah nota',
          body: 'Tulis balasan, atau tukar ke Nota peribadi untuk mesej yang hanya dilihat pasukan anda. Taip / untuk balasan pantas; lampirkan fail dengan klip.',
        },
        s6: {
          title: 'Butiran',
          body: 'Siapa pelanggan, pasukan dan orang yang memiliki chat, label, nombor penghantar, dan ringkasan kes.',
        },
      },
    },
    contacts: {
      title: 'Kenalan',
      intro: 'Buku pelanggan anda — import, cari dan buka kenalan.',
      steps: {
        s1: {
          title: 'Semua kenalan anda',
          body: 'Semua orang yang anda hubungi, untuk cawangan yang anda pilih. Klik kenalan untuk membuka profil penuhnya.',
        },
        s2: {
          title: 'Import atau tambah',
          body: 'Import hamparan (.csv atau .xlsx), eksport telefon (.vcf) atau Google Contacts anda. Pendua dipadankan mengikut telefon dan e-mel, tidak pernah digandakan.',
        },
        s3: {
          title: 'Cari dan isih',
          body: 'Taip untuk menapis mengikut nama, telefon, tag atau entiti; klik tajuk lajur untuk mengisih.',
        },
      },
    },
    contact: {
      title: 'Profil kenalan',
      intro: 'Semua tentang seorang pelanggan dalam satu halaman.',
      steps: {
        s1: {
          title: 'Siapa mereka',
          body: 'Nama, telefon, e-mel dan tag. “Buka dalam Chat” melompat ke perbualan mereka.',
        },
        s2: {
          title: 'Ringkasan AI',
          body: 'Ringkasan pendek setiap panggilan, chat dan e-mel dengan orang ini — jana sebelum anda menelefon mereka semula.',
        },
        s3: {
          title: 'Komunikasi',
          body: 'Setiap mesej dan panggilan merentas semua saluran, yang terbaru dahulu.',
        },
        s4: {
          title: 'AI untuk kenalan ini',
          body: 'Ikut suis chat itu sendiri, atau tentukan untuk orang ini: AI tidak pernah menjawab mereka, atau sentiasa menjawab.',
        },
        s5: {
          title: 'Saluran',
          body: 'Di mana anda boleh menghubungi mereka — WhatsApp, e-mel, suara dan lain-lain — dengan kesihatan setiap saluran. Jemput mereka ke aplikasi lain dengan pautan atau kod QR.',
        },
      },
    },
    journeys: {
      title: 'Perjalanan — susulan automatik',
      intro: 'Mesej, panggilan dan semakan yang berjalan sendiri.',
      steps: {
        s1: {
          title: 'Apa itu perjalanan',
          body: 'Perjalanan bermula pada pencetus (kenalan baharu, tag, mesej, tarikh) dan kemudian menghantar mesej, menunggu, membuat keputusan dan menyerahkan kepada manusia — dengan sendirinya.',
        },
        s2: {
          title: 'Perjalanan anda',
          body: 'Pilih perjalanan untuk menyuntingnya, atau mulakan yang baharu daripada templat seperti siri alu-aluan atau peringatan janji temu.',
        },
        s3: {
          title: 'Bina langkah',
          body: 'Tambah langkah mengikut urutan: hantar mesej, tunggu, semak syarat atau biar AI memutuskan, panggil, kemas kini tag. Kanvas Lanjutan menunjukkan perjalanan yang sama sebagai rajah.',
        },
        s4: {
          title: 'Lancarkan dan pantau',
          body: 'Perjalanan baharu bermula dalam keadaan dijeda. Lancarkan apabila sedia, dan buka Larian untuk melihat siapa di mana dan mengapa ada yang tercicir.',
        },
      },
    },
    aiMonitor: {
      title: 'Pemantau AI',
      intro: 'Lihat, semak dan luluskan apa yang AI katakan.',
      steps: {
        s1: {
          title: 'Suis utama',
          body: 'Hidupkan atau matikan AI untuk semua, atau beri jadual (contohnya waktu malam dan hujung minggu sahaja).',
        },
        s2: {
          title: 'Bahagian',
          body: 'Menunggu semakan memegang balasan yang patut disemak manusia dahulu. Aktiviti AI ialah semua yang ditulis AI. Pembelajaran dan Statistik menunjukkan bagaimana ia bertambah baik.',
        },
        s3: {
          title: 'Mod semakan',
          body: 'Dalam Tetapan, pilih: hantar terus, pegang hanya balasan berisiko (harga, bayaran balik, janji), pegang semua, atau mod bayangan (AI mendraf, manusia menghantar).',
        },
        s4: {
          title: 'Ajar ia',
          body: 'Luluskan, sunting atau tolak balasan yang dipegang — setiap keputusan menjadi pelajaran yang digunakan AI pada masa akan datang.',
        },
      },
    },
    playground: {
      title: 'Ruang uji AI',
      intro: 'Berbual dengan AI seperti pelanggan — tiada apa yang dihantar.',
      steps: {
        s1: {
          title: 'Sediakan suasana',
          body: 'Pilih cawangan, saluran dan pelanggan — rekaan, kenalan sebenar, atau salinan perbualan sebenar.',
        },
        s2: {
          title: 'Chat',
          body: 'Taip sebagai pelanggan. AI menjawab sama seperti dalam produksi, tetapi tiada apa yang dihantar, disimpan atau ditempah.',
        },
        s3: {
          title: 'Pemeriksa',
          body: 'Apa yang menjadi asas balasan (pengetahuan, pelajaran, alat) dan apa yang akan berlaku dalam produksi: hantar, pegang untuk semakan, atau serahkan.',
        },
        s4: {
          title: 'Cuba perubahan, simpan yang baik',
          body: 'Tukar model atau sunting persona dan bandingkan dua tetapan sebelah-menyebelah. “Simpan sebagai persona” menjadikan draf yang baik sebagai yang sebenar; “Ajar” menukar pembetulan menjadi pelajaran.',
        },
      },
    },
    persona: {
      title: 'Persona & peraturan rumah',
      intro: 'Cara AI memperkenalkan diri, dan peraturan yang sentiasa dipatuhinya.',
      steps: {
        s1: {
          title: 'Persona',
          body: 'Siapa AI itu dan bagaimana bunyinya. Satu persona sahaja terpakai: milik cawangan sendiri, jika tiada milik ibu pejabat, jika tiada milik organisasi.',
        },
        s2: {
          title: 'Peraturan rumah',
          body: 'Peraturan yang mesti dipatuhi setiap balasan, seperti “Sentiasa bersopan”. Ia bertambah — cawangan mengekalkan semua peraturan di atasnya dan boleh menambah sendiri.',
        },
        s3: {
          title: 'Apa yang AI terima',
          body: 'Permulaan tepat setiap prompt: peraturan keselamatan, kemudian peraturan rumah, kemudian persona. Cuba di ruang uji sebelum dilancarkan.',
        },
      },
    },
    knowledge: {
      title: 'Pangkalan pengetahuan',
      intro: 'Fakta yang menjadi sumber jawapan AI.',
      steps: {
        s1: {
          title: 'Apa yang AI tahu',
          body: 'Fakta, polisi, Soalan Lazim, acara dan notis pendek. AI hanya menyatakan apa yang ada di sini — ia tidak pernah mereka harga atau tarikh.',
        },
        s2: {
          title: 'Tambah atau muat naik',
          body: 'Tambah item secara manual atau muat naik fail PDF, teks atau CSV. Notis dan acara boleh bermula dan tamat pada tarikh tertentu, jadi ia luput sendiri.',
        },
        s3: {
          title: 'Uji carian',
          body: 'Taip soalan pelanggan untuk melihat item mana yang akan dibaca AI. Tambah sinonim pada tag (dalam setiap bahasa pelanggan anda) apabila item yang betul terlepas.',
        },
      },
    },
    skills: {
      title: 'Kemahiran AI',
      intro: 'Apa yang boleh dibuat AI, dan semakan yang mesti dilepasi setiap balasan.',
      steps: {
        s1: {
          title: 'Empat jenis kemahiran',
          body: 'Arahan (cara mengendalikan situasi), alat (mencari sesuatu), semakan (peraturan yang mesti dilepasi setiap balasan) dan aplikasi bersambung (menempah janji temu dalam kalendar).',
        },
        s2: {
          title: 'Cipta dengan wizard',
          body: 'Terangkan apa yang anda mahu dalam bahasa biasa; wizard bertanya beberapa soalan dan mendraf kemahiran untuk anda uji.',
        },
        s3: {
          title: 'Alat terbina dalam',
          body: 'Alat tarikh dan masa serta alat serahan sentiasa aktif — AI menggunakannya untuk mendapatkan tarikh yang betul dan memanggil manusia apabila perlu.',
        },
      },
    },
    channels: {
      title: 'Saluran',
      intro: 'Setiap nombor, peti mel dan aplikasi yang digunakan perniagaan.',
      steps: {
        s1: {
          title: 'Peti masuk anda',
          body: 'Setiap nombor WhatsApp, peti mel, bot, halaman dan widget laman web ialah kad dengan status kesihatannya.',
        },
        s2: {
          title: 'Tambah saluran',
          body: 'Pilih aplikasi, sambungkan langkah demi langkah, kemudian uji. TikTok dan Douyin memerlukan kelulusan platform terlebih dahulu.',
        },
        s3: {
          title: 'Siapa yang menjawab',
          body: 'Bagi setiap peti masuk, pilih pasukan yang menerima chat baharu, sama ada AI menjawab secara lalai, dan cara pelanggan tidak dikenali dikenal pasti.',
        },
      },
    },
    billing: {
      title: 'Bil AI',
      intro: 'Jumlah yang perlu dibayar setiap syarikat untuk penggunaan AI.',
      steps: {
        s1: {
          title: 'Bulan dan syarikat',
          body: 'Langkah melalui bulan dan pilih syarikat. Cawangan dibilkan kepada syarikatnya.',
        },
        s2: {
          title: 'Penyata',
          body: 'Penggunaan mengikut ciri, yuran bulanan dan jumlah, dalam mata wang bil anda.',
        },
        s3: {
          title: 'Cetak atau tetapkan terma',
          body: 'Cetak menyimpan penyata sebagai PDF. Pengurus bil menetapkan markup, yuran dan mata wang di Terma bil.',
        },
      },
    },
    settings: {
      title: 'Tetapan',
      intro: 'Pilihan anda sendiri dan konfigurasi organisasi.',
      steps: {
        s1: {
          title: 'Bahagian',
          body: 'Halaman Peribadi hanya untuk anda (rupa, bahasa, pemberitahuan, sambungan Google anda). Halaman Organisasi dan AI memerlukan kebenaran — ikon kunci menunjukkannya.',
        },
        s2: {
          title: 'Di mana yang lain',
          body: 'Saluran, templat, pengetahuan, kemahiran, pengguna dan entiti ada di bar sisi, bukan di sini.',
        },
      },
    },
  },
}
