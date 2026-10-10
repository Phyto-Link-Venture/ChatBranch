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
    replayHint: 'Main semula panduan ini bila-bila masa melalui butang ? di bahagian atas halaman.',
  },
  manual: {
    title: 'Manual pengguna ChatBranch',
    subtitle: 'Panduan praktikal untuk setiap bahagian ChatBranch — baca di sini, muat turun, atau letak di laman web anda sendiri.',
    contents: 'Kandungan',
    showMe: 'Tunjukkan dalam aplikasi',
    backToApp: 'Kembali ke ChatBranch',
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
      title: 'Papan pemuka — halaman utama anda',
      intro: 'Prestasi perbualan, panggilan dan AI.',
      steps: {
        s1: {
          title: 'Halaman utama anda',
          body: 'Papan pemuka ialah perkara pertama yang anda lihat selepas log masuk: ringkasan tempoh yang anda pilih, untuk cawangan yang anda pilih.',
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
    notifications: {
      title: 'Pemberitahuan',
      intro: 'Semua yang memerlukan perhatian anda, dalam satu senarai.',
      steps: {
        s1: {
          title: 'Peti pemberitahuan anda',
          body: 'Mesej pelanggan baharu, masalah saluran, penyerahan perjalanan dan berita pentadbir terkumpul di sini. Nombor di sebelah Pemberitahuan di bar sisi menunjukkan berapa yang belum dibaca.',
        },
        s2: {
          title: 'Kategori',
          body: 'Klik satu kategori — Mesej pelanggan, Kesihatan saluran dan lain-lain — untuk melihat jenis itu sahaja. Semua menunjukkan semuanya semula.',
        },
        s3: {
          title: 'Cari dan tapis',
          body: 'Taip perkataan untuk mencari, pilih untuk melihat yang belum dibaca atau sudah dibaca sahaja, atau pilih julat tarikh dari–hingga.',
        },
        s4: {
          title: 'Senarai',
          body: 'Dikumpulkan mengikut hari, terbaru dahulu. Klik pemberitahuan untuk membuka perkara berkaitan. Tandakan kotak untuk menanda atau memadam beberapa sekali gus.',
        },
        s5: {
          title: 'Kemaskan',
          body: 'Tandakan semua dibaca mengosongkan kiraan belum dibaca. Padam yang dibaca membuang yang sudah anda lihat (anda akan ditanya dahulu).',
        },
        s6: {
          title: 'Pilih apa yang anda terima',
          body: 'Ikon gear membuka tetapan pemberitahuan anda sendiri, tempat anda menghidupkan atau mematikan setiap jenis pemberitahuan.',
        },
      },
    },
    appearance: {
      title: 'Rupa',
      intro: 'Jadikan ChatBranch kelihatan seperti yang anda suka.',
      steps: {
        s1: {
          title: 'Untuk anda sahaja',
          body: 'Semua di halaman ini hanya mengubah skrin anda sendiri — orang lain mengekalkan pilihan mereka.',
        },
        s2: {
          title: 'Bahasa',
          body: 'Pilih English, Bahasa Melayu atau 简体中文. Seluruh aplikasi bertukar serta-merta.',
        },
        s3: {
          title: 'Pratonton',
          body: 'Contoh langsung tajuk, butang dan pautan. Ia berubah sebaik sahaja anda memilih sesuatu di bawah.',
        },
        s4: {
          title: 'Tema',
          body: 'Tiga rupa: Kertas & Dakwat, Grafit Pro dan Blok Lembut. Klik satu untuk mencubanya — anda sentiasa boleh menukar semula.',
        },
        s5: {
          title: 'Mod',
          body: 'Cerah, Gelap, atau Sistem untuk mengikut tetapan telefon atau komputer anda.',
        },
        s6: {
          title: 'Warna aksen',
          body: 'Warna sorotan butang dan pautan. ChatBranch melaraskannya sedikit jika perlu supaya teks kekal mudah dibaca.',
        },
        s7: {
          title: 'Saiz teks, bucu dan fon',
          body: 'Besarkan atau kecilkan teks, jadikan bucu lebih bulat atau tajam, dan pilih fon tajuk.',
        },
        s8: {
          title: 'Tetapkan semula atau kongsi',
          body: 'Tetapkan semula kepada lalai membatalkan perubahan anda. Pentadbir boleh klik Guna sebagai lalai organisasi supaya semua yang belum memilih sendiri mendapat rupa ini.',
        },
      },
    },
    chats: {
      title: 'Sembang — peti masuk bersatu',
      intro: 'Setiap saluran dalam satu peti masuk: balas, serahkan, dan biarkan AI membantu.',
      steps: {
        s1: {
          title: 'Cari perbualan',
          body: 'Taip nama, nombor telefon atau apa-apa perkataan daripada sembang dalam kotak ini. Senarai di bawah kemudian hanya menunjukkan sembang yang sepadan.',
        },
        s2: {
          title: 'Tapis',
          body: 'Klik Tapis untuk mengecilkan senarai — mengikut saluran, status, siapa yang ditugaskan, AI hidup atau mati, menunggu balasan dan lain-lain. Nombor kecil menunjukkan berapa penapis yang aktif.',
        },
        s3: {
          title: 'Paparan',
          body: 'Semua, Milik saya, Belum ditugaskan dan Perlukan manusia ialah paparan sedia ada. Tetapkan beberapa penapis, kemudian gunakan menu ••• untuk menyimpannya sebagai tab anda sendiri.',
        },
        s4: {
          title: 'Senarai perbualan',
          body: 'Aktiviti terbaru dahulu. Masa berwarna oren bermaksud pelanggan sedang menunggu anda. Klik sembang untuk membukanya — atau tekan Pilih di atas untuk menanda beberapa sembang dan menghidupkan atau mematikan AI, menyelesaikan atau menugaskan semuanya sekali gus.',
        },
        s5: {
          title: 'AI hidup atau mati untuk sembang ini',
          body: 'Matikan AI untuk menjawab sendiri, atau hidupkan supaya ia membalas. Jika peraturan di atas yang menentukan, suis dikunci dan memberitahu sebabnya. Selesaikan, di sebelahnya, menutup sembang apabila anda selesai.',
        },
        s6: {
          title: 'Apabila manusia diperlukan',
          body: 'Bar kuning ini muncul apabila AI meminta bantuan atau peraturan menyerahkan sembang. Klik Ambil alih untuk menjawab sendiri, atau Biar AI teruskan untuk memulangkan sembang kepada AI.',
        },
        s7: {
          title: 'Balas atau tambah nota',
          body: 'Tulis balasan, atau tukar ke Nota peribadi untuk mesej yang hanya dilihat pasukan anda. Enter menghantar, Shift+Enter menambah baris baharu. Taip / untuk balasan pantas; lampirkan fail dengan klip.',
        },
        s8: {
          title: 'Butiran',
          body: 'Siapa pelanggan, pasukan dan orang yang memiliki sembang, label, nombor penghantar, dan ringkasan kes. Pada telefon atau tablet, bukanya dengan butang Butiran perbualan di pengepala sembang.',
        },
      },
    },
    contacts: {
      title: 'Kenalan',
      intro: 'Buku pelanggan anda — import, cari dan buka kenalan.',
      steps: {
        s1: {
          title: 'Semua kenalan anda',
          body: 'Semua orang yang anda hubungi, bagi cawangan yang anda pilih di atas. Baris di bawah tajuk menyatakan jumlahnya.',
        },
        s2: {
          title: 'Import',
          body: 'Klik Import untuk membawa masuk hamparan (.csv atau .xlsx), eksport telefon (.vcf) atau Google Contacts anda. Wizard ringkas memadankan lajur; pendua dikesan melalui telefon dan e-mel, tidak pernah digandakan.',
        },
        s3: {
          title: 'Tambah kenalan',
          body: 'Klik Tambah kenalan untuk menaip seorang secara manual — nama, telefon, e-mel dan tag.',
        },
        s4: {
          title: 'Kecilkan senarai',
          body: 'Tunjukkan satu entiti, satu tag atau satu jenis rekod sahaja. Klik Kosongkan untuk melihat semua orang semula.',
        },
        s5: {
          title: 'Cari dan susun',
          body: 'Taip dalam kotak carian untuk mencari mengikut nama, telefon, tag atau entiti; klik tajuk lajur untuk menyusun. Klik baris untuk membuka kenalan itu.',
        },
        s6: {
          title: 'Butang baris',
          body: 'Lihat membuka profil penuh. Pensel menyunting kenalan dan tong sampah memadamnya — anda sentiasa ditanya dahulu.',
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
    bookings: {
      title: 'Tempahan',
      intro: 'Setiap janji temu yang ditempah oleh AI dan pasukan anda.',
      steps: {
        s1: {
          title: 'Semua janji temu',
          body: 'Janji temu yang ditempah dalam sembang — di Google Calendar atau disimpan dalam ChatBranch — bagi cawangan yang anda pilih.',
        },
        s2: {
          title: 'Akan datang atau lepas',
          body: 'Akan datang menunjukkan apa yang masih belum berlaku, yang terdekat dahulu. Lepas menunjukkan apa yang sudah berlaku.',
        },
        s3: {
          title: 'Senarai',
          body: 'Bila, dengan siapa, kalendar mana, dan siapa yang menempah (AI atau seseorang). Cari mengikut kenalan atau tajuk di atas senarai.',
        },
        s4: {
          title: 'Penapis dan muat semula',
          body: 'Di sebelah kotak carian anda boleh menunjukkan satu status atau satu kalendar sahaja. Butang ini memuat semula senarai.',
        },
        s5: {
          title: 'Batal atau susulan',
          body: 'Klik Batal pada tempahan akan datang untuk mengosongkan masa itu — anda ditanya dahulu. Ikon sembang membuka perbualan dengan pelanggan itu.',
        },
      },
    },
    assignments: {
      title: 'Penetapan akaun',
      intro: 'Nombor atau peti mel mana yang bercakap dengan setiap pelanggan.',
      steps: {
        s1: {
          title: 'Apa halaman ini buat',
          body: 'Setiap pelanggan dipautkan kepada satu nombor penghantar bagi setiap saluran, supaya mereka sentiasa menerima mesej daripada nombor yang sama. Halaman ini menunjukkan pautan itu dan membolehkan anda mengubahnya.',
        },
        s2: {
          title: 'Pilih saluran',
          body: 'Pilih saluran (WhatsApp, e-mel dan lain-lain), kemudian cari kenalan mengikut nama atau telefon.',
        },
        s3: {
          title: 'Satu kad bagi setiap nombor',
          body: 'Berapa kenalan yang dijaga setiap nombor, dengan kesihatannya. Klik Urus kenalan untuk memindahkan orang atau menyematkan mereka pada satu nombor.',
        },
        s4: {
          title: 'Seimbangkan sama rata',
          body: 'Menyebarkan kenalan yang tidak disemat secara sama rata ke nombor yang sihat. Kenalan yang disemat tidak pernah berpindah.',
        },
        s5: {
          title: 'Peraturan penghalaan',
          body: 'Cara nombor anda bekerjasama: kelajuan AI membalas, apa berlaku apabila nombor terputus, pengawal mesej sejuk, dan perkataan STOP / START tambahan.',
        },
        s6: {
          title: 'Simpan',
          body: 'Perubahan pada peraturan penghalaan hanya berkuat kuasa selepas anda klik Simpan peraturan penghalaan.',
        },
      },
    },
    journeys: {
      title: 'Perjalanan — susulan automatik',
      intro: 'Mesej, panggilan dan semakan yang berjalan sendiri.',
      steps: {
        s1: {
          title: 'Apa itu perjalanan',
          body: 'Perjalanan bermula dengan pencetus (kenalan baharu, tag, mesej, tarikh) dan kemudian menghantar mesej, menunggu, membuat keputusan dan menyerahkan kepada manusia — dengan sendirinya.',
        },
        s2: {
          title: 'Perjalanan baharu',
          body: 'Klik Perjalanan baharu dan pilih templat sedia ada — siri alu-aluan, peringatan janji temu, tarik semula dan lain-lain — atau mula dari halaman kosong.',
        },
        s3: {
          title: 'Perjalanan anda',
          body: 'Perjalanan dibuka pada senarai semua perjalanan ini: apa yang memulakannya, sama ada ia aktif dan berapa ramai yang masuk. Klik Buka (atau barisnya) untuk menyuntingnya. Di dalam perjalanan, Semua perjalanan membawa anda kembali ke sini; pensel menamakannya semula dan menu ••• menyalin, mengeksport atau memadamnya.',
        },
        s4: {
          title: 'Langkah atau kanvas',
          body: 'Langkah menunjukkan perjalanan sebagai senarai ringkas yang dibaca dari atas ke bawah. Kanvas lanjutan menunjukkan perjalanan yang sama sebagai rajah — gunakan yang lebih mudah bagi anda.',
        },
        s5: {
          title: 'Bina langkah',
          body: 'Tambah langkah mengikut urutan: hantar mesej, tunggu, semak syarat atau biar AI memutuskan, buat panggilan, kemas kini tag. Klik mana-mana langkah untuk mengubahnya.',
        },
        s6: {
          title: 'Aktifkan',
          body: 'Perjalanan baharu bermula dalam keadaan dijeda, jadi tiada apa dihantar semasa anda membina. Klik Aktifkan apabila sedia; klik Aktif sekali lagi untuk menjedanya.',
        },
        s7: {
          title: 'Pantau hasilnya',
          body: 'Larian menunjukkan berapa orang telah bermula, di mana setiap orang sekarang, dan mengapa ada yang tercicir.',
        },
      },
    },
    automations: {
      title: 'Automasi',
      intro: 'Peraturan ringkas “apabila ini berlaku, lakukan itu” untuk perbualan.',
      steps: {
        s1: {
          title: 'Apa itu automasi',
          body: 'Peraturan yang berjalan sendiri pada perbualan — contohnya “apabila sembang baharu mengandungi perkataan bayaran balik, tugaskan kepada pasukan Bil”.',
        },
        s2: {
          title: 'Apabila, Jika, lakukan',
          body: 'Setiap peraturan ada tiga bahagian: Apabila (peristiwa), Jika (syarat pilihan) dan lakukan (tindakan). Baris ini menerangkan idea itu dalam satu ayat.',
        },
        s3: {
          title: 'Tambah peraturan',
          body: 'Klik Tambah peraturan, beri nama, pilih peristiwa, tambah syarat jika perlu dan pilih apa yang patut berlaku.',
        },
        s4: {
          title: 'Peraturan anda',
          body: 'Setiap peraturan menunjukkan peristiwa, syarat dan tindakannya sebagai cip kecil, serta berapa kali ia telah berjalan, dengan nombor kedudukannya. Setiap peraturan yang sepadan berjalan, dari atas ke bawah — seret peraturan atau gunakan anak panah untuk menukar susunan.',
        },
        s5: {
          title: 'Hidup, mati, sunting',
          body: 'Uji menyemak peraturan pada perbualan sebenar tanpa menghantar atau mengubah apa-apa. Hidup / Mati menjeda peraturan tanpa memadamnya. Pensel menyuntingnya; tong sampah memadamnya.',
        },
        s6: {
          title: 'Peraturan atau perjalanan?',
          body: 'Gunakan automasi untuk tindak balas pantas dalam sembang. Gunakan Perjalanan untuk siri mesej dan panggilan berjadual yang lebih panjang kepada kenalan.',
        },
      },
    },
    templates: {
      title: 'Templat',
      intro: 'Mesej dan skrip siap tulis — ditulis sekali, digunakan di mana-mana.',
      steps: {
        s1: {
          title: 'Tulis sekali, guna semula',
          body: 'Simpan jawapan yang anda berikan berulang kali, dan gunakannya semula dalam sembang, perjalanan dan panggilan tanpa menaipnya setiap kali.',
        },
        s2: {
          title: 'Tiga jenis',
          body: 'Mesej ialah balasan pantas untuk sembang. Skrip panggilan dibaca perkataan demi perkataan dalam panggilan automatik. WhatsApp (diluluskan Meta) ialah templat yang mesti diluluskan Meta sebelum anda boleh menghantar mesej dahulu.',
        },
        s3: {
          title: 'Cipta satu',
          body: 'Klik Balasan pantas baharu (atau Skrip panggilan baharu) untuk menulis satu. Beri kod pendek supaya ejen boleh mencarinya dengan cepat dalam kotak balasan.',
        },
        s4: {
          title: 'Templat anda',
          body: 'Cari, tapis mengikut saluran, dan klik mana-mana templat untuk menyuntingnya. Pemboleh ubah, seperti nama pertama pelanggan, diisi secara automatik apabila mesej dihantar.',
        },
        s5: {
          title: 'Petua',
          body: 'Dalam sembang, taip / diikuti kod pendek untuk memasukkan balasan pantas ke dalam kotak balasan.',
        },
      },
    },
    tags: {
      title: 'Tag',
      intro: 'Label untuk kenalan, entiti dan perbualan.',
      steps: {
        s1: {
          title: 'Kegunaan tag',
          body: 'Tag mengumpulkan perkara supaya anda boleh mencari, menapis dan mengautomasikannya — contohnya “VIP”, “Borong” atau “Perlu susulan”.',
        },
        s2: {
          title: 'Tag untuk siapa',
          body: 'Tag dimiliki oleh Kenalan, Entiti atau Perbualan. Klik satu untuk melihat tag itu sahaja.',
        },
        s3: {
          title: 'Tag baharu',
          body: 'Klik Tag baharu, pilih nama, warna dan kegunaannya. Anda boleh menambah medan tambahan (seperti nombor keahlian) dan meletakkan tag dalam kumpulan yang hanya satu boleh digunakan.',
        },
        s4: {
          title: 'Tag anda',
          body: 'Tag anda, dikumpulkan, dengan medan tambahan dan entiti pemilik setiap satu.',
        },
        s5: {
          title: 'Guna dan sunting',
          body: 'Kiraan menunjukkan berapa kenalan atau entiti yang membawa tag itu — klik untuk melihatnya. Pensel menyunting tag; tong sampah memadamnya.',
        },
      },
    },
    callLogs: {
      title: 'Log panggilan',
      intro: 'Setiap panggilan yang selesai dengan ringkasan dan transkripnya.',
      steps: {
        s1: {
          title: 'Panggilan mana',
          body: 'Semua panggilan, Telefon (SIP) atau WhatsApp — klik satu untuk menunjukkan jenis itu sahaja. Nombor menunjukkan jumlahnya.',
        },
        s2: {
          title: 'Panggilan selesai',
          body: 'Terbaru dahulu, dengan kenalan, nombor, masa dan hasil.',
        },
        s3: {
          title: 'Buka panggilan',
          body: 'Klik panggilan untuk melihat ringkasan AI dan transkrip penuh — siapa berkata apa. Klik sekali lagi untuk menutup.',
        },
        s4: {
          title: 'Membuat panggilan',
          body: 'Panggilan dimulakan dari sembang (butang telefon) atau oleh perjalanan. Apabila panggilan tamat, ia muncul di sini.',
        },
      },
    },
    aiMonitor: {
      title: 'Pemantau AI',
      intro: 'Lihat, semak dan luluskan apa yang dikatakan AI.',
      steps: {
        s1: {
          title: 'Suis utama',
          body: 'Hidupkan atau matikan AI untuk semua orang, atau beri ia jadual (contohnya waktu petang dan hujung minggu sahaja).',
        },
        s2: {
          title: 'Menunggu semakan',
          body: 'Balasan yang ditulis AI yang patut disemak seseorang dahulu. Luluskan menghantarnya, Sunting membolehkan anda membetulkan perkataan, Tolak membuangnya — dan setiap pilihan mengajar AI.',
        },
        s3: {
          title: 'Aktiviti AI',
          body: 'Semua yang ditulis AI, dihantar atau tidak, terbaru dahulu. Buka satu untuk melihat asas balasan itu.',
        },
        s4: {
          title: 'Pembelajaran',
          body: 'Pelajaran yang dipelajari AI daripada ibu jari ke atas, ibu jari ke bawah dan suntingan. Bergantung pada mod pembelajaran, seseorang meluluskan setiap pelajaran sebelum AI menggunakannya.',
        },
        s5: {
          title: 'Semakan AI',
          body: 'Biar AI yang lebih kuat menyemak sampel balasan terkini berbanding pengetahuan, peraturan rumah dan persona anda. Ia menandakan kesilapan dan mencadangkan apa yang perlu ditambah — pelajaran, item pengetahuan atau peraturan rumah — dengan satu klik. Ia hanya berjalan apabila anda mengklik Semak sekarang.',
        },
        s6: {
          title: 'Statistik',
          body: 'Berapa balasan yang dihantar, ditahan atau ditolak, berapa kerap orang menyukainya, dan berapa kerap sembang diserahkan kepada manusia.',
        },
        s7: {
          title: 'Tetapan — mod semakan',
          body: 'Pilih tahap berhati-hati AI: hantar terus, tahan balasan berisiko sahaja (harga, bayaran balik, janji), tahan semua, atau mod bayangan (AI mendraf, manusia menghantar).',
        },
        s8: {
          title: 'Cuba dengan selamat',
          body: 'Membuka ruang uji AI, tempat anda bersembang dengan AI seperti pelanggan — tiada apa dihantar kepada sesiapa.',
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
      intro: 'Fakta yang digunakan AI untuk menjawab.',
      steps: {
        s1: {
          title: 'Apa yang AI tahu',
          body: 'Fakta, polisi, Soalan Lazim, acara dan notis ringkas. AI hanya menyatakan apa yang ada di sini — ia tidak pernah mereka harga atau tarikh.',
        },
        s2: {
          title: 'Tambah atau muat naik',
          body: 'Entri baharu membolehkan anda menaip fakta, polisi, Soalan Lazim, acara atau notis. Muat naik dokumen membaca fail PDF, teks atau CSV untuk anda. Acara dan notis boleh bermula dan tamat pada tarikh tertentu, jadi ia luput sendiri.',
        },
        s3: {
          title: 'Tapis mengikut jenis',
          body: 'Klik satu jenis untuk menunjukkan fakta, polisi, Soalan Lazim, acara atau notis sahaja. Semua menunjukkan semuanya semula.',
        },
        s4: {
          title: 'Entri anda',
          body: 'Klik entri untuk menyuntingnya. Suis hidup/mati menyimpan entri sementara AI berhenti menggunakannya.',
        },
        s5: {
          title: 'Uji carian',
          body: 'Taip soalan pelanggan untuk melihat entri mana yang akan dibaca AI. Jika entri yang betul terlepas, tambah perkataan yang digunakan pelanggan anda (dalam setiap bahasa) pada tagnya.',
        },
      },
    },
    skills: {
      title: 'Kemahiran AI',
      intro: 'Apa yang AI boleh buat, dan semakan yang mesti dilepasi setiap balasan.',
      steps: {
        s1: {
          title: 'Empat jenis kemahiran',
          body: 'Arahan (cara mengendalikan sesuatu situasi), alat (mencari maklumat), semakan (peraturan yang mesti dilepasi setiap balasan) dan aplikasi bersambung (menempah janji temu dalam kalendar).',
        },
        s2: {
          title: 'Cipta dengan wizard',
          body: 'Klik Kemahiran baharu dan terangkan apa yang anda mahu dengan perkataan biasa. Wizard bertanya beberapa soalan, menulis kemahiran itu dan membolehkan anda mengujinya sebelum menyimpan.',
        },
        s3: {
          title: 'Alat terbina dalam',
          body: 'Alat tarikh dan masa serta alat penyerahan sentiasa hidup — AI menggunakannya untuk mendapatkan tarikh yang betul dan memanggil manusia apabila perlu.',
        },
        s4: {
          title: 'Arahan',
          body: 'Nasihat dalam bahasa biasa untuk satu situasi — contohnya “apabila seseorang meminta bayaran balik, tanya nombor pesanan dahulu”.',
        },
        s5: {
          title: 'Aplikasi bersambung',
          body: 'Beri AI kalendar supaya ia boleh menawarkan masa lapang dan menempah janji temu. Semua yang ditempahnya muncul di halaman Tempahan.',
        },
        s6: {
          title: 'Alat',
          body: 'Program kecil yang boleh dijalankan AI untuk mencari atau mengira sesuatu. Wizard boleh menulisnya untuk anda.',
        },
        s7: {
          title: 'Semakan balasan',
          body: 'Peraturan yang mesti dilepasi setiap balasan sebelum dihantar, seperti “jangan sesekali menjanjikan diskaun”. Balasan yang gagal ditulis semula, ditahan atau diserahkan kepada manusia.',
        },
        s8: {
          title: 'Hidup, mati, sunting',
          body: 'Suis menghidupkan atau mematikan kemahiran tanpa memadamnya. Pensel menyuntingnya; tong sampah memadamnya.',
        },
      },
    },
    voice: {
      title: 'Suara & panggilan',
      intro: 'Suara yang digunakan AI dalam panggilan telefon dan WhatsApp.',
      steps: {
        s1: {
          title: 'Halaman ini',
          body: 'Pilih dan urus suara yang digunakan AI apabila menelefon pelanggan. Ini untuk panggilan sahaja — persona sembang berada di bawah Persona & peraturan.',
        },
        s2: {
          title: 'Personaliti',
          body: 'Setiap suara membawa salah satu daripada empat personaliti ini, yang membentuk cara AI bercakap dalam panggilan.',
        },
        s3: {
          title: 'Tambah suara',
          body: 'Klik + Tambah suara untuk merakam atau memuat naik sampel pendek dan mengklon suara anda sendiri, atau import satu daripada penyedia suara anda.',
        },
        s4: {
          title: 'Cari suara',
          body: 'Tapis mengikut jantina, bahasa, loghat, sumber atau personaliti. Kiraan di sebelah kanan menunjukkan berapa suara yang sepadan.',
        },
        s5: {
          title: 'Pilih suara',
          body: 'Tekan Main pratonton untuk mendengar suara, kemudian klik kadnya. Pilihan pentadbir disimpan sebagai suara bagi setiap panggilan AI; kempen masih boleh memilih suara lain.',
        },
        s6: {
          title: 'Sebutan',
          body: 'Ajar AI cara menyebut perkataan sukar — nama jenama, nama tempat, nama orang — supaya panggilan kedengaran betul.',
        },
      },
    },
    aiProviders: {
      title: 'Kunci & model AI',
      intro: 'Perkhidmatan AI mana yang menulis balasan, dan kunci yang diperlukannya.',
      steps: {
        s1: {
          title: 'Kegunaan halaman ini',
          body: 'Beritahu ChatBranch syarikat AI mana yang hendak digunakan dan tampal kuncinya di sini. Tanpa kunci, AI menjawab dalam mod ujian ringkas.',
        },
        s2: {
          title: 'Sedia atau mati',
          body: 'Hijau bermaksud balasan AI sudah sedia. Kelabu bermaksud belum ada kunci, jadi AI tidak dapat menulis balasan sebenar.',
        },
        s3: {
          title: 'Kunci, penyedia dan model',
          body: 'Tampal kunci daripada syarikat AI anda, pilih penyedia (atau Automatik untuk mencubanya mengikut urutan) dan pilih model.',
        },
        s4: {
          title: 'Dari mana nilai datang',
          body: 'Setiap medan menyatakan “daripada Tetapan” (disimpan di sini), “daripada .env” (ditetapkan pada pelayan) atau “belum ditetapkan”.',
        },
        s5: {
          title: 'Tetapan lanjutan',
          body: 'Penyedia tambahan, embedding (untuk carian pengetahuan yang lebih bijak) dan pemasaan. Tetapan lalai sesuai untuk kebanyakan perniagaan.',
        },
        s6: {
          title: 'Simpan',
          body: 'Selepas mengubah apa-apa, klik Simpan perubahan di bahagian atas halaman. Buang perubahan membatalkan suntingan anda.',
        },
      },
    },
    channels: {
      title: 'Saluran — peti masuk anda',
      intro: 'Setiap nombor, peti mel dan aplikasi yang digunakan perniagaan untuk berhubung.',
      steps: {
        s1: {
          title: 'Peti masuk anda',
          body: 'Setiap nombor WhatsApp, peti mel, bot, halaman dan sembang laman web yang dijawab pasukan anda ada di halaman ini.',
        },
        s2: {
          title: 'Tambah peti masuk',
          body: 'Klik Tambah peti masuk untuk menyambung yang baharu. Panel langkah demi langkah membimbing anda: pilih aplikasi → sambungkan → pilih siapa menjawab → uji.',
        },
        s3: {
          title: 'Disambungkan',
          body: 'Setiap kad ialah satu peti masuk, dengan titik berwarna untuk kesihatannya — hijau berfungsi, kuning atau merah perlu perhatian.',
        },
        s4: {
          title: 'Tunjuk satu jenis',
          body: 'Klik satu saluran untuk melihat peti masuk itu sahaja; Semua menunjukkan semuanya semula.',
        },
        s5: {
          title: 'Butang kad',
          body: 'Tetapan mengubah peti masuk dan siapa yang menjawabnya (pasukan, AI hidup atau mati, pelanggan tidak dikenali). Uji menyemak sambungan sekarang juga. Pasangkan menunjukkan kod QR untuk nombor WhatsApp peribadi.',
        },
        s6: {
          title: 'Tambah saluran',
          body: 'Setiap aplikasi yang boleh disambung, dikumpulkan mengikut jenis. Klik satu untuk bermula. TikTok dan Douyin memerlukan kelulusan platform dahulu.',
        },
        s7: {
          title: 'Lanjutan',
          body: 'Alamat webhook Meta dan tetapan mel lalai. Anda jarang memerlukannya — pembangun anda mungkin memintanya sekali.',
        },
      },
    },
    integrations: {
      title: 'Integrasi',
      intro: 'Sambungkan Google, Shopify, Stripe dan aplikasi lain.',
      steps: {
        s1: {
          title: 'Sambungan dikongsi',
          body: 'Sambungan yang digunakan seluruh organisasi atau satu entiti — contohnya Google Calendar syarikat tempat AI menempah janji temu.',
        },
        s2: {
          title: 'Siapa menggunakan sambungan yang mana',
          body: 'Apabila sesuatu memerlukan aplikasi, ChatBranch mencuba sambungan anda sendiri dahulu, kemudian entiti anda, kemudian induknya, kemudian organisasi. Kotak ini menerangkannya.',
        },
        s3: {
          title: 'Sambungkan integrasi',
          body: 'Satu kad bagi setiap aplikasi, menunjukkan sama ada ia sudah disambungkan.',
        },
        s4: {
          title: 'Butang sambung',
          body: 'Klik Sambungkan untuk log masuk ke aplikasi dan pilih untuk siapa sambungan itu. “Sediakan (pentadbir, sekali sahaja)” bermaksud pentadbir mesti memasukkan kunci aplikasi dahulu.',
        },
        s5: {
          title: 'Akaun yang disambungkan',
          body: 'Setiap sambungan yang telah dibuat, siapa yang menggunakannya dan statusnya, dengan butang untuk mengurusnya.',
        },
      },
    },
    connections: {
      title: 'Sambungan saya',
      intro: 'Sambungkan akaun Google anda sendiri.',
      steps: {
        s1: {
          title: 'Untuk anda sahaja',
          body: 'Pautkan produk Google anda sendiri. Hanya anda yang menggunakannya — contohnya, AI menempah dalam kalendar anda sendiri untuk sembang yang ditugaskan kepada anda.',
        },
        s2: {
          title: 'Akaun Google saya',
          body: 'Setiap produk Google yang boleh anda sambungkan, dan kegunaannya.',
        },
        s3: {
          title: 'Status',
          body: 'Tanda hijau Disambungkan bermaksud ia berfungsi. Jika ia perlu disambung semula, log masuk sekali lagi.',
        },
        s4: {
          title: 'Sambung',
          body: 'Klik Sambung dan log masuk dengan Google. Sambung semula menyegarkannya; Putuskan sambungan menarik balik kebenaran anda pada bila-bila masa.',
        },
        s5: {
          title: 'Sambungan mana yang digunakan',
          body: 'ChatBranch menggunakan sambungan anda sendiri dahulu, kemudian sambungan dikongsi daripada entiti anda, induknya dan akhirnya seluruh organisasi.',
        },
      },
    },
    users: {
      title: 'Pengguna & peranan',
      intro: 'Siapa boleh log masuk dan apa yang boleh dibuat setiap orang.',
      steps: {
        s1: {
          title: 'Semua yang log masuk',
          body: 'Tambah kakitangan, tukar peranan mereka, dan matikan log masuk apabila seseorang berhenti.',
        },
        s2: {
          title: 'Apa setiap peranan boleh buat',
          body: 'Peringatan ringkas tentang tiga peranan terbina dalam. Peranan tersuai dibuat di bawah Kebenaran.',
        },
        s3: {
          title: 'Tambah pengguna',
          body: 'Klik Tambah pengguna, kemudian masukkan nama pengguna, kata laluan dan peranan.',
        },
        s4: {
          title: 'Senarai pengguna',
          body: 'Cari mengikut nama, peranan atau entiti. Superadmin boleh menukar peranan seseorang terus dari lajur Peranan.',
        },
        s5: {
          title: 'Butang baris',
          body: 'Akses entiti memilih cawangan yang dilihat ejen. Tetapkan semula kata laluan menetapkan kata laluan baharu. Nyahaktifkan menyekat log masuk tetapi menyimpan semua sejarah.',
        },
      },
    },
    teams: {
      title: 'Pasukan',
      intro: 'Kumpulan orang yang berkongsi kerja.',
      steps: {
        s1: {
          title: 'Apa itu pasukan',
          body: 'Kumpulan ejen — contohnya Jualan atau Sokongan. Sembang baharu boleh pergi kepada pasukan dahulu.',
        },
        s2: {
          title: 'Cara pasukan berfungsi',
          body: 'Sembang dimiliki oleh pasukan dan, jika mahu, seorang dalam pasukan itu. Dengan agihan bergilir dihidupkan, sembang baharu diagihkan satu demi satu.',
        },
        s3: {
          title: 'Tambah pasukan',
          body: 'Klik Tambah pasukan, beri nama, pilih entitinya, tandakan ahlinya, dan pilih sama ada perbualan baharu diagihkan secara bergilir.',
        },
        s4: {
          title: 'Pasukan anda',
          body: 'Ahli, sembang terbuka dan entiti bagi setiap pasukan.',
        },
        s5: {
          title: 'Sunting atau padam',
          body: 'Pensel mengubah pasukan; tong sampah memadamnya.',
        },
      },
    },
    entities: {
      title: 'Entiti',
      intro: 'Organisasi, syarikat, cawangan dan outlet anda sebagai pokok.',
      steps: {
        s1: {
          title: 'Bahagian perniagaan anda',
          body: 'Setiap sembang, kenalan dan banyak tetapan dimiliki oleh satu entiti, supaya setiap cawangan melihat kerjanya sendiri.',
        },
        s2: {
          title: 'Sebuah pokok',
          body: 'Entiti utama berada di atas; cawangan dan outlet berada di bawahnya. Yang di atas melihat semua di bawahnya — tidak pernah sebaliknya.',
        },
        s3: {
          title: 'Kiraan',
          body: 'Berapa entiti utama dan sub-entiti yang anda ada.',
        },
        s4: {
          title: 'Tambah entiti',
          body: 'Klik Tambah entiti, beri nama dan pilih Induknya. Biarkan Induk kosong untuk membuat entiti utama baharu.',
        },
        s5: {
          title: 'Senarai',
          body: 'Pokok mengikut urutan, dengan induk dan tag setiap entiti. Pensel menyunting; tong sampah memadam (hanya apabila tiada apa di bawahnya).',
        },
      },
    },
    bindings: {
      title: 'Pautan',
      intro: 'Pautkan orang, pasukan, kenalan, tag dan entiti di satu tempat.',
      steps: {
        s1: {
          title: 'Siapa milik apa',
          body: 'Satu halaman untuk setiap pautan — pengguna mana melihat entiti mana, siapa dalam pasukan mana, kenalan mana membawa tag, dan sebagainya.',
        },
        s2: {
          title: '1 · Apa yang hendak dipautkan',
          body: 'Mula-mula pilih jenis pautan yang hendak diubah, contohnya pengguna dan entiti.',
        },
        s3: {
          title: '2 · Pilih satu',
          body: 'Kemudian pilih satu item untuk diusahakan — contohnya satu entiti.',
        },
        s4: {
          title: '3 · Tandakan ahli',
          body: 'Hidupkan atau matikan ahli. Setiap kad kumpulan menunjukkan Tiada, sebahagian atau Semua; Urus membuka senarai penuh.',
        },
        s5: {
          title: 'Simpan',
          body: 'Tiada apa berubah sehingga anda klik Simpan perubahan pada bar di bawah. Buang perubahan membatalkan suntingan anda.',
        },
      },
    },
    permissions: {
      title: 'Kebenaran',
      intro: 'Apa sebenarnya yang boleh dilihat dan dibuat setiap peranan.',
      steps: {
        s1: {
          title: 'Peranan menentukan akses',
          body: 'Setiap orang ada peranan, dan peranan menentukan apa yang boleh mereka lihat dan ubah. Hanya superadmin boleh menyunting peranan.',
        },
        s2: {
          title: 'Peranan',
          body: 'Satu kad bagi setiap peranan, dengan bilangan pengguna dan bilangan bahagian yang boleh digunakannya. Peranan Sistem tidak boleh dinamakan semula atau dipadam.',
        },
        s3: {
          title: 'Tambah peranan',
          body: 'Klik Tambah peranan untuk membuat peranan tersuai, contohnya “Ketua pasukan”.',
        },
        s4: {
          title: 'Urus akses',
          body: 'Membuka kebenaran peranan sebagai grid suis — satu untuk setiap bahagian ChatBranch.',
        },
        s5: {
          title: 'Hidupkan atau matikan bahagian',
          body: 'Hidupkan atau matikan setiap kebenaran, atau gunakan Tanda semua. Klik Simpan perubahan apabila selesai.',
        },
        s6: {
          title: 'Perlu tahu',
          body: 'Pelayan sentiasa menyemak kebenaran — menyembunyikan item menu bukan satu-satunya perlindungan.',
        },
      },
    },
    developers: {
      title: 'Kunci API & MCP',
      intro: 'Benarkan perisian lain dan aplikasi AI bekerja dengan ChatBranch.',
      steps: {
        s1: {
          title: 'Untuk menyambung sistem lain',
          body: 'Jika anda bukan orang teknikal, biasanya anda boleh serahkan halaman ini kepada pembangun anda.',
        },
        s2: {
          title: 'Kunci API',
          body: 'Kunci membolehkan program lain bertindak sebagai anda. Setiap kunci boleh dihadkan kepada bahagian yang diperlukannya sahaja.',
        },
        s3: {
          title: 'Cipta kunci',
          body: 'Klik Cipta kunci, beri nama dan pilih aksesnya. Kunci ditunjukkan SEKALI sahaja — salin ke tempat selamat dengan segera.',
        },
        s4: {
          title: 'Sambungkan aplikasi AI',
          body: 'Arahan langkah demi langkah untuk menyambung ChatBranch ke aplikasi AI seperti Claude, supaya ia boleh membaca dan bertindak dengan kebenaran anda.',
        },
        s5: {
          title: 'Pelayan MCP luar',
          body: 'Beri pembantu Tanya ChatBranch alatan daripada sistem lain. Ia sentiasa bertanya sebelum mengubah apa-apa.',
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
        s4: {
          title: 'Kuota',
          body: 'Had perbelanjaan AI bulanan untuk syarikat, cawangan dan pengguna. Kuning dari 80%; pada 100% AI dijeda dan sembang diserahkan kepada orang sehingga bulan depan atau sehingga had dinaikkan.',
        },
      },
    },
    billingSettings: {
      title: 'Bil AI',
      intro: 'Terma yang digunakan untuk mengebil setiap syarikat bagi penggunaan AI.',
      steps: {
        s1: {
          title: 'Kegunaan halaman ini',
          body: 'Tetapkan cara penggunaan AI menjadi bil bagi setiap syarikat: mata wang, kadar pertukaran, markup dan yuran bulanan.',
        },
        s2: {
          title: 'Terma bil',
          body: 'Mata wang lalai, kadar pertukaran daripada dolar AS, peratusan markup dan yuran bulanan. Buka bil membawa anda ke halaman bil AI.',
        },
        s3: {
          title: 'Contoh',
          body: 'Contoh pengiraan dengan nombor anda, supaya anda boleh menyemak hasilnya munasabah.',
        },
        s4: {
          title: 'Terma setiap syarikat',
          body: 'Beri satu syarikat markup atau yuran sendiri. Biarkan kotak kosong untuk menggunakan nilai lalai.',
        },
        s5: {
          title: 'Simpan',
          body: 'Klik Simpan terma bil untuk menggunakan perubahan anda.',
        },
        s6: {
          title: 'Bulan lalu dikunci',
          body: 'Perubahan terpakai pada bil bulan ini dan seterusnya. Bulan-bulan sebelumnya kekal dengan terma yang terpakai ketika itu — senarai menunjukkan terma bagi setiap bulan.',
        },
      },
    },
    activity: {
      title: 'Aktiviti',
      intro: 'Rekod siapa melakukan apa, dan bila.',
      steps: {
        s1: {
          title: 'Log aktiviti',
          body: 'Setiap perubahan oleh orang, kunci API, aplikasi AI, pembantu, automasi, perjalanan dan webhook ditulis di sini.',
        },
        s2: {
          title: 'Julat masa',
          body: 'Pilih tempoh sedia ada atau pilih tarikh anda sendiri.',
        },
        s3: {
          title: 'Penapis',
          body: 'Cari mengikut perkataan, atau pilih orang, tindakan, entiti atau sumber untuk mengecilkan senarai.',
        },
        s4: {
          title: 'Entri',
          body: 'Terbaru dahulu. Klik mana-mana baris untuk melihat butiran penuh perubahan itu.',
        },
        s5: {
          title: 'Eksport CSV',
          body: 'Muat turun apa yang anda lihat sebagai fail hamparan.',
        },
      },
    },
    testLab: {
      title: 'Makmal ujian',
      intro: 'Semak bahawa ChatBranch berfungsi, dengan selamat, menggunakan data rekaan.',
      steps: {
        s1: {
          title: 'Apa itu makmal ujian',
          body: 'Semakan berskrip yang menjalankan sistem sebenar dengan data ujian yang diasingkan daripada pelanggan anda. Hanya superadmin melihat halaman ini.',
        },
        s2: {
          title: 'Jalankan ujian',
          body: 'Jalankan semua memulakan setiap semakan; Jalankan yang dipilih hanya menjalankan yang anda tandakan. Baris di atas menyatakan sama ada data ujian wujud dan sama ada kunci AI sebenar digunakan.',
        },
        s3: {
          title: 'Kes penggunaan',
          body: 'Semakan dikumpulkan mengikut bahagian. Tandakan yang anda mahu, jalankan satu dengan Jalankan, atau buka satu untuk melihat setiap perkara yang disemaknya.',
        },
        s4: {
          title: 'Sejarah larian',
          body: 'Larian terkini anda. Klik satu untuk melihat keputusannya semula.',
        },
        s5: {
          title: 'Muatkan data demo',
          body: 'Mengisi kawasan ujian dengan contoh kenalan dan sembang yang boleh anda terokai.',
        },
        s6: {
          title: 'Set semula data ujian',
          body: 'Memadam semua yang dibuat oleh makmal ujian — dan tiada yang lain.',
        },
      },
    },
    settings: {
      title: 'Tetapan',
      intro: 'Pilihan peribadi anda dan konfigurasi organisasi.',
      steps: {
        s1: {
          title: 'Bahagian',
          body: 'Halaman Peribadi hanya untuk anda (rupa, bahasa, pemberitahuan, sambungan Google anda sendiri). Halaman Organisasi dan AI memerlukan kebenaran — ikon kunci menunjukkan yang mana.',
        },
        s2: {
          title: 'Halaman ini',
          body: 'Nama halaman yang sedang anda buka, dan satu baris tentang fungsinya.',
        },
        s3: {
          title: 'Buat perubahan',
          body: 'Ubah apa yang anda perlukan di sini. Jika anda tidak dapat membuka sesuatu halaman, minta kebenaran yang diperlukan daripada pentadbir.',
        },
        s4: {
          title: 'Simpan',
          body: 'Apabila halaman ada perubahan yang belum disimpan, Simpan perubahan dan Buang perubahan muncul di sini. Tiada apa disimpan sehingga anda klik Simpan perubahan.',
        },
        s5: {
          title: 'Ke mana yang lain pergi',
          body: 'Saluran, templat, pengetahuan, kemahiran, pengguna dan entiti berada di bar sisi, bukan di sini.',
        },
      },
    },
  },
}
