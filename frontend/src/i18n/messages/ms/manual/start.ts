import type { start as source } from '../../en/manual/start'
import type { Dict } from '../../../types'

export const start: Dict<typeof source> = {
  firstSignIn: {
    title: 'Log masuk buat kali pertama',
    summary: 'Buka ChatBranch, log masuk dengan nama pengguna dan kata laluan yang diberikan kepada anda, dan kenali laman utama. Mengambil masa kira-kira dua minit.',
    before:
      'Anda perlukan alamat web ChatBranch (contohnya https://chat.kedaianda.com) serta nama pengguna dan kata laluan. Akaun dicipta oleh pentadbir anda — tiada butang “Daftar”. Komputer atau telefon dengan pelayar terkini (Chrome, Edge, Safari atau Firefox) sudah memadai.',
    steps: {
      s1: {
        title: 'Buka halaman log masuk',
        body: 'Taip alamat ChatBranch dalam pelayar anda dan tekan Enter. Anda akan melihat logo ChatBranch dan kad bertajuk “Selamat kembali”.',
      },
      s2: {
        title: 'Pilih bahasa anda (pilihan)',
        body: 'Di bawah kad, klik “English”, “Bahasa Melayu” atau “简体中文”. Halaman bertukar serta-merta. Pilihan ini hanya diingati pada pelayar ini sehingga anda log masuk.',
      },
      s3: {
        title: 'Taip nama pengguna dan kata laluan',
        body: 'Klik kotak “Nama pengguna” dan taip nama pengguna anda (contohnya siti). Kemudian taip kata laluan dalam kotak “Kata laluan”. Klik ikon mata untuk menyemak apa yang anda taip.',
      },
      s4: {
        title: 'Klik “Log masuk”',
        body: 'Butang menjadi aktif sebaik kedua-dua kotak diisi. Satu pemutar kecil muncul sementara ChatBranch menyemak butiran anda.',
      },
      s5: {
        title: 'Anda tiba di Papan pemuka',
        body: 'Papan pemuka ialah laman utama: ringkasan perbualan, panggilan dan AI untuk beberapa hari lepas. Jika peranan anda tidak boleh melihat Papan pemuka, anda dibawa ke halaman pertama yang dibenarkan (selalunya Sembang).',
      },
      s6: {
        title: 'Ikut (atau langkau) panduan halaman',
        body: 'Kali pertama anda membuka sesuatu halaman, panduan kecil muncul dan menyerlahkan bahagiannya satu demi satu. Klik “Seterusnya” untuk teruskan, “Kembali” untuk berundur, atau “Langkau” untuk menutupnya. Ia tidak akan muncul lagi pada halaman itu.',
      },
      s7: {
        title: 'Main semula panduan bila-bila masa',
        body: 'Klik butang ? di bar atas (namanya “Panduan”). Pilih “Tunjukkan panduan halaman ini” untuk memainkannya semula, “Lawatan ChatBranch” untuk lawatan seluruh skrin, atau “Buka manual pengguna” untuk manual ini.',
      },
      s8: {
        title: 'Hentikan panduan daripada muncul sendiri (pilihan)',
        body: 'Dalam menu ? yang sama, matikan “Tunjukkan panduan pada lawatan pertama”. Anda masih boleh memainkan semula mana-mana panduan dari menu itu.',
      },
      s9: {
        title: 'Log keluar apabila selesai',
        body: 'Di bahagian bawah bar sisi, di sebelah nama dan peranan anda, klik ikon pintu dengan anak panah (“Log keluar”). Pada telefon, ketik “Lagi” dahulu untuk membuka menu. Sentiasa log keluar pada komputer yang dikongsi.',
      },
    },
    done: 'Anda nampak nama dan peranan anda di bahagian bawah kiri bar sisi dan halaman Papan pemuka di tengah skrin.',
    tips: {
      t1: 'Selepas log masuk, tukar bahasa anda secara kekal di Tetapan → Rupa — ia akan mengikut anda ke mana-mana peranti.',
      t2: 'Tandakan alamat ini, atau pada telefon gunakan “Pasang aplikasi” di bahagian bawah menu untuk mendapatkan ikon ChatBranch di skrin utama.',
      t3: 'Anda boleh log masuk pada komputer dan telefon pada masa yang sama.',
    },
    trouble:
      'Ralat muncul di bawah kata laluan: semak ejaan dan Caps Lock, kemudian cuba lagi; hanya pentadbir boleh menetapkan semula kata laluan yang terlupa. “Sesi anda telah tamat — sila log masuk semula”: anda telah dilog keluar (contohnya selepas rehat panjang) — log masuk semula sahaja. Halaman langsung tidak dimuatkan: semak sambungan internet anda dan sahkan alamat dengan pentadbir.',
  },
  findYourWay: {
    title: 'Kenali skrin ChatBranch',
    summary: 'Pelajari bahagian skrin yang anda gunakan setiap hari: bar sisi, kotak carian, cip Langsung, loceng dan, pada telefon, bar tab di bahagian bawah.',
    steps: {
      s1: {
        title: 'Lihat bar sisi',
        body: 'Di sebelah kiri ialah bar sisi. Di atas sekali ada “Papan pemuka” dan “Pemberitahuan”, diikuti kumpulan: “Kerja” (Sembang, Kenalan, Tempahan…), “Automasi”, “Pustaka”, “Saluran”, “Pentadbiran” dan “Semakan”. Anda hanya nampak halaman yang dibenarkan oleh peranan anda.',
      },
      s2: {
        title: 'Lipat kumpulan yang jarang digunakan',
        body: 'Klik nama kumpulan, contohnya “Pustaka”, untuk melipatnya; klik sekali lagi untuk membukanya. Anak panah kecil di sebelah nama menunjukkan sama ada ia terbuka.',
      },
      s3: {
        title: 'Kecilkan bar sisi',
        body: 'Klik ikon panel di atas bar sisi, di sebelah logo (“Kuncupkan bar sisi”). Hanya ikon yang tinggal — halakan tetikus pada ikon untuk melihat namanya. Klik ikon itu sekali lagi (“Kembangkan bar sisi”) untuk memaparkan semula nama.',
      },
      s4: {
        title: 'Cari Tetapan dan nama anda',
        body: '“Tetapan” berada di bahagian paling bawah bar sisi. Di bawahnya ialah nama anda, peranan anda, butang matahari/bulan untuk mod cerah atau gelap, dan “Log keluar”.',
      },
      s5: {
        title: 'Buka carian (palet arahan)',
        body: 'Klik kotak carian di atas (“Cari kenalan, sembang, aliran, templat…”) atau tekan Ctrl+K (⌘K pada Mac) dari mana-mana. Tetingkap carian terbuka di atas halaman.',
      },
      s6: {
        title: 'Cari dan lompat',
        body: 'Taip nama, nombor telefon atau nama halaman, contohnya “siti” atau “templat”. Halaman, kenalan dan perbualan yang sepadan muncul mengikut kumpulan. Gunakan kekunci anak panah dan tekan Enter, atau klik hasil untuk membukanya. Esc menutup tetingkap.',
      },
      s7: {
        title: 'Semak cip Langsung',
        body: 'Di bar atas, cip “Langsung” menunjukkan berapa panggilan sedang berjalan dan berapa sembang menunggu balasan. Titiknya bermaksud “Langsung — bersambung” apabila mesej baharu tiba serta-merta. Klik untuk melihat sembang yang menunggu dan panggilan aktif, kemudian “Buka Sembang”.',
      },
      s8: {
        title: 'Semak loceng',
        body: 'Loceng di atas kanan menunjukkan nombor apabila ada pemberitahuan belum dibaca. Klik untuk melihat yang terkini, “Tandakan semua dibaca”, atau “Lihat semua” untuk membuka halaman Pemberitahuan.',
      },
      s9: {
        title: 'Pada telefon: guna tab bawah',
        body: 'Pada skrin kecil, bar sisi disembunyikan. Gunakan bar tab di bawah — “Utama”, “Sembang”, “Kenalan” dan “Lagi”. “Lagi” membuka menu penuh, termasuk penukar entiti dan “Log keluar”.',
      },
    },
    done: 'Anda boleh membuka mana-mana halaman dari bar sisi atau dengan Ctrl+K, dan anda tahu di mana sembang yang menunggu dan pemberitahuan baharu dipaparkan.',
    tips: {
      t1: 'Ctrl+K ialah cara terpantas ke mana-mana — taip beberapa huruf nama halaman atau nama pelanggan dan tekan Enter.',
      t2: 'Kumpulan yang dilipat dan bar sisi yang dikecilkan diingati pada pelayar ini.',
      t3: 'Ctrl+J (⌘J pada Mac) membuka Tanya ChatBranch, pembantu anda — lihat “Dapatkan bantuan daripada Tanya ChatBranch”.',
    },
    trouble:
      'Halaman yang anda perlukan tiada dalam bar sisi, atau anda nampak “Anda tiada akses kepada …”: peranan anda tidak merangkuminya — minta pentadbir memberikannya di Pengguna & peranan. Cip Langsung menunjukkan “Luar talian — kemas kini dijeda”: sambungan anda terputus; ia akan bersambung semula sendiri apabila rangkaian pulih.',
  },
  pickEntity: {
    title: 'Pilih cawangan yang anda lihat',
    summary: 'Jika perniagaan anda mempunyai beberapa syarikat, cawangan atau outlet (ChatBranch memanggilnya entiti), gunakan penukar di bar atas untuk memilih sembang, kenalan dan nombor siapa yang dipaparkan oleh setiap halaman.',
    steps: {
      s1: {
        title: 'Cari penukar',
        body: 'Di bar atas, di sebelah kiri butang berkilau, ada kotak dengan nama dan anak panah kecil — itulah entiti yang sedang anda lihat. Pada telefon, ketik “Lagi” di bawah; penukar berada di atas menu.',
      },
      s2: {
        title: 'Buka senarai',
        body: 'Klik kotak itu. Senarai terbuka dengan “Semua entiti” di atas dan setiap entiti yang boleh anda lihat di bawahnya. Entiti utama mempunyai lencana “Utama”; cawangannya diinden di bawahnya.',
      },
      s3: {
        title: 'Tunjukkan satu entiti sahaja',
        body: 'Klik NAMA entiti. Senarai ditutup dan setiap halaman kini menunjukkan entiti itu sahaja (serta cawangannya). Tanda semak muncul di sebelah namanya apabila anda membuka senarai lagi.',
      },
      s4: {
        title: 'Lihat beberapa sekali gus',
        body: 'Sebaliknya, klik KOTAK SEMAK kecil di hadapan nama. Entiti itu ditambah ke paparan dan senarai kekal terbuka, jadi anda boleh menanda lagi. Kotak di bar atas kemudian menunjukkan, contohnya, “2 entiti”. Klik kotak semak yang bertanda untuk membuangnya semula.',
      },
      s5: {
        title: 'Cawangan ikut serta secara automatik',
        body: 'Menanda entiti utama turut merangkumi semua cawangannya — kotak semak cawangan kelihatan bertanda dan kelabu (“Termasuk bersama …”). Anda tidak perlu menandanya satu persatu.',
      },
      s6: {
        title: 'Lihat semuanya',
        body: 'Klik “Semua entiti” di atas senarai untuk melihat setiap entiti yang dibenarkan sekali gus.',
      },
      s7: {
        title: 'Cari dalam senarai panjang (pilihan)',
        body: 'Apabila terdapat lebih daripada enam entiti, kotak “Cari entiti” muncul di atas senarai. Taip sebahagian nama untuk menapis.',
      },
      s8: {
        title: 'Pilihan anda diingati',
        body: 'ChatBranch mengingati pilihan anda pada pelayar ini. Kali seterusnya anda log masuk di sini, anda bermula dengan entiti yang sama.',
      },
    },
    done: 'Kotak di bar atas menunjukkan nama entiti (atau “2 entiti”, atau “Semua entiti”) dan halaman dimuat semula dengan data itu sahaja.',
    tips: {
      t1: 'Ibu pejabat nampak sembang dan kenalan cawangannya; cawangan tidak pernah nampak milik ibu pejabat atau cawangan lain.',
      t2: 'Untuk mengubah tetapan entiti itu sendiri (contohnya persona AI), pilih SATU entiti — jika beberapa ditanda, halaman tetapan menunjukkan tahap organisasi.',
      t3: 'Jika senarai kelihatan kosong, semak penukar dahulu — mungkin anda sedang melihat cawangan lain.',
    },
    trouble:
      'Kotak tiada anak panah dan tidak boleh diklik: anda hanya boleh melihat satu entiti, jadi tiada apa untuk ditukar. Entiti tiada dalam senarai: anda tidak ditugaskan kepadanya — minta pentadbir (Pentadbiran → Pautan atau Pengguna & peranan). Notis mengatakan beberapa entiti ditanda: pilih satu entiti dalam penukar untuk melihat atau mengubah tetapannya sendiri.',
  },
  languageAndLook: {
    title: 'Tukar bahasa dan rupa',
    summary: 'Pilih bahasa papan pemuka, tema, mod cerah atau gelap, warna aksen dan saiz teks yang selesa. Pilihan anda disimpan pada profil, jadi ia mengikut anda ke mana-mana peranti.',
    steps: {
      s1: {
        title: 'Buka Tetapan → Rupa',
        body: 'Klik “Tetapan” di bahagian bawah bar sisi. Di bawah “Peribadi”, klik “Rupa”.',
      },
      s2: {
        title: 'Pilih bahasa anda',
        body: 'Dalam kad “Bahasa” di atas, klik “English”, “Bahasa Melayu” atau “简体中文”. Seluruh papan pemuka bertukar serta-merta dan anda nampak “Bahasa disimpan.”.',
      },
      s3: {
        title: 'Pilih tema',
        body: 'Di bawah “Tema”, klik salah satu daripada sepuluh kad — contohnya “Kertas & Dakwat” (kertas hangat, lalai), “Lautan”, “Tengah Malam” (cantik dalam mod gelap) atau “Kontras tinggi” (paling mudah dibaca). Setiap kad menunjukkan pratonton kecil. Aplikasi berubah serta-merta — tiada butang Simpan.',
      },
      s4: {
        title: 'Pilih cerah atau gelap',
        body: 'Di bawah “Mod”, klik “Cerah”, “Gelap” atau “Sistem”. “Sistem” mengikut tetapan komputer atau telefon anda, bertukar gelap pada waktu malam jika peranti anda berbuat demikian.',
      },
      s5: {
        title: 'Besarkan atau kecilkan teks',
        body: 'Di bawah “Saiz teks”, klik S, M, L atau XL, atau seret peluncur ke sebarang saiz antara 85 % dan 130 %. Semua di skrin membesar atau mengecil — cuba L jika teks terasa kecil.',
      },
      s6: {
        title: 'Pilih warna aksen (pilihan)',
        body: 'Klik “Tetapan lanjutan” untuk membukanya. Di bawah “Warna aksen”, klik salah satu titik warna, atau “Lalai tema” untuk kembali. ChatBranch melaraskannya sedikit supaya teks kekal mudah dibaca.',
      },
      s7: {
        title: 'Fon, jarak dan susun atur (pilihan)',
        body: 'Juga dalam “Tetapan lanjutan”: “Tipografi” (fon badan, fon tajuk, tinggi baris), “Jarak” (kepadatan, lebar halaman, margin halaman, lapik kad), “Susun atur” (bar sisi di kiri atau kanan, atau bar atas — komputer sahaja) dan “Butiran” (bucu, gaya kad, gerakan). Kad pratonton di atas menunjukkan rupanya.',
      },
      s8: {
        title: 'Tukar cerah/gelap dengan pantas',
        body: 'Di mana-mana dalam aplikasi, klik butang matahari/bulan di bahagian bawah bar sisi, di sebelah nama anda. Ia bertukar antara cerah dan gelap tanpa mengubah rupa lain anda.',
      },
      s9: {
        title: 'Mula semula jika anda tidak suka',
        body: 'Di bahagian bawah halaman Rupa, klik “Tetapkan semula kepada lalai organisasi” (atau “Tetapkan semula kepada lalai” jika organisasi anda tiada lalai) untuk kembali kepada rupa organisasi (atau Kertas & Dakwat, cerah).',
      },
    },
    done: 'Papan pemuka memaparkan bahasa dan warna pilihan anda, dan ia kekal apabila anda log masuk pada peranti lain.',
    tips: {
      t1: 'Pentadbir boleh klik “Guna sebagai lalai organisasi” (dan “Jadikan ini lalai organisasi” untuk bahasa) supaya pengguna baharu bermula dengan rupa ini.',
      t2: 'Menukar bahasa anda tidak mengubah apa yang pelanggan lihat — AI membalas dalam bahasa pelanggan.',
      t3: 'Mod gelap lebih selesa untuk mata pada syif petang.',
    },
    trouble:
      '“Digunakan pada peranti ini — tidak dapat disimpan pada profil anda.”: rupa berfungsi di sini, tetapi pelayan tidak dapat dihubungi; cuba lagi kemudian supaya ia mengikut anda ke peranti lain. Bahasa bertukar balik selepas log masuk: anda memilihnya di halaman log masuk, yang hanya mengingatinya pada pelayar itu — pilih semula di Tetapan → Rupa.',
  },
  myNotifications: {
    title: 'Urus pemberitahuan anda',
    summary: 'Baca dan kemas amaran anda — mesej pelanggan baharu, masalah saluran, penyerahan kepada manusia — dan pilih jenis yang sampai kepada anda.',
    steps: {
      s1: {
        title: 'Buka loceng',
        body: 'Klik loceng di atas kanan. Nombor merah bermaksud ada amaran belum dibaca. Senarai ringkas amaran terkini terbuka.',
      },
      s2: {
        title: 'Buka amaran',
        body: 'Klik amaran untuk terus ke perkara berkaitan — contohnya perbualan dengan pelanggan yang menulis. Klik “Tandakan semua dibaca” untuk mengosongkan nombor itu.',
      },
      s3: {
        title: 'Buka halaman Pemberitahuan',
        body: 'Klik “Lihat semua” di bahagian bawah loceng, atau “Pemberitahuan” berhampiran atas bar sisi. Anda nampak semua amaran, yang terbaharu dahulu.',
      },
      s4: {
        title: 'Tapis senarai',
        body: 'Gunakan “Kategori” (contohnya “Mesej pelanggan” atau “Kesihatan saluran”), pilihan “Belum dibaca” / “Dibaca”, tarikh, atau kotak carian untuk mencari amaran. “Kosongkan penapis” memaparkan semula semuanya.',
      },
      s5: {
        title: 'Kemaskan',
        body: 'Gunakan butang kecil di sebelah kanan amaran untuk menandanya dibaca atau belum dibaca, atau untuk memadamnya. “Tandakan semua dibaca” mengosongkan semuanya; “Padam yang dibaca” membuang amaran yang sudah dibaca (perbualan itu sendiri tidak terjejas).',
      },
      s6: {
        title: 'Buka tetapan pemberitahuan anda',
        body: 'Klik “Tetapan pemberitahuan” di halaman Pemberitahuan, atau pergi ke Tetapan → Peribadi → “Pemberitahuan saya”.',
      },
      s7: {
        title: 'Matikan yang tidak diperlukan',
        body: 'Dalam “Amaran yang saya terima”, gunakan suis di sebelah setiap jenis: “Mesej pelanggan”, “Kesihatan saluran”, “Aliran & automasi”, “Pentadbiran & konfigurasi” dan “Sistem”. Setiap perubahan disimpan serta-merta dan hanya melibatkan anda.',
      },
      s8: {
        title: 'Perbualan anda sahaja (pilihan)',
        body: 'Di bawah “Skop mesej pelanggan”, hidupkan “Hanya perbualan yang ditugaskan kepada saya atau pasukan saya” jika anda pentadbir dan tidak mahu amaran untuk sembang yang belum ditugaskan.',
      },
      s9: {
        title: 'Semak ia berfungsi',
        body: 'Apabila amaran seterusnya daripada jenis yang anda biarkan hidup tiba, loceng menunjukkan nombor semula.',
      },
    },
    done: 'Nombor pada loceng hilang, dan suis di bawah “Amaran yang saya terima” menunjukkan tepat jenis yang anda mahu.',
    tips: {
      t1: 'Amaran mesej baharu dihantar kepada ejen yang ditugaskan dahulu, kemudian pasukannya, kemudian pentadbir — jadi menugaskan sembang mengurangkan gangguan untuk semua.',
      t2: 'Beberapa mesej dalam perbualan yang sama digabungkan menjadi satu amaran belum dibaca.',
      t3: 'Biarkan “Kesihatan saluran” hidup jika anda menjaga nombor WhatsApp — ia memberitahu apabila satu nombor terputus.',
    },
    trouble:
      'Suis kelabu dengan “Disenyapkan untuk seluruh organisasi oleh pentadbir.”: jenis itu dimatikan untuk semua — minta pentadbir (Tetapan → Lalai pemberitahuan). Anda tidak pernah menerima amaran mesej: pastikan “Mesej pelanggan” hidup dan sembang ditugaskan kepada anda atau pasukan anda.',
  },
  askChatBranch: {
    title: 'Dapatkan bantuan daripada Tanya ChatBranch',
    summary: 'Tanya pembantu terbina dalam cara melakukan sesuatu, cari maklumat, atau minta ia menyediakan perubahan untuk anda. Ia bertindak sebagai anda, dan tiada apa berubah sehingga anda klik “Sahkan”.',
    steps: {
      s1: {
        title: 'Buka pembantu',
        body: 'Klik butang berkilau di bar atas (“Tanya ChatBranch”), atau tekan Ctrl+J (⌘J pada Mac). Pada komputer, anda juga boleh halakan tetikus pada tab nipis di tepi kanan skrin dan klik. Panel terbuka di sebelah kanan.',
      },
      s2: {
        title: 'Cuba cadangan',
        body: 'Di bawah “Apa yang boleh saya bantu?” ada butang contoh seperti “Berapa banyak sembang yang menunggu balasan?” atau “Bagaimana saya menyambungkan WhatsApp?”. Klik satu untuk bertanya.',
      },
      s3: {
        title: 'Tanya soalan anda sendiri',
        body: 'Taip dalam kotak di bawah (“Tanya apa sahaja tentang ChatBranch…”), contohnya “Cari nombor telefon Siti Aminah”, dan tekan Enter. Shift+Enter memulakan baris baharu.',
      },
      s4: {
        title: 'Minta ia melakukan sesuatu',
        body: 'Taip tugasan, contohnya “Matikan AI untuk sembang ini” atau “Tag Ahmad sebagai VIP”. Pembantu menyediakannya tetapi belum melaksanakannya.',
      },
      s5: {
        title: 'Baca kad pengesahan',
        body: 'Kad bertanda “Perlukan pengesahan anda” menunjukkan tepat apa yang akan berubah. Klik “Butiran” untuk melihat lebih lanjut.',
      },
      s6: {
        title: 'Sahkan atau batal',
        body: 'Klik “Sahkan” untuk melaksanakannya — kad kemudian menunjukkan “Selesai” — atau “Batal” untuk membatalkannya. Kad yang dibiarkan kira-kira 15 minit akan tamat tempoh; tanya sekali lagi sahaja.',
      },
      s7: {
        title: 'Cari sembang terdahulu',
        body: 'Klik ikon jam di atas panel (“Sejarah”) untuk melihat sembang lepas anda — hanya anda yang boleh melihatnya. Klik satu untuk menyambungnya. Ikon gelembung bertanda tambah (“Sembang baharu”) memulakan yang baharu.',
      },
      s8: {
        title: 'Alihkan ke tepi (pilihan)',
        body: 'Pada komputer, klik “Terapung — alihkan ke mana-mana” di kepala panel dan seret tetingkap melalui bar atasnya. Klik “Lekatkan di sisi” untuk meletakkannya semula.',
      },
      s9: {
        title: 'Tutup',
        body: 'Tekan Esc atau klik X. Sembang anda disimpan dan dibuka semula di tempat anda berhenti.',
      },
    },
    done: 'Pembantu menjawab soalan anda, atau kad menunjukkan “Selesai” selepas anda klik “Sahkan”.',
    tips: {
      t1: 'Ia hanya boleh melakukan apa yang dibenarkan oleh peranan anda sendiri, dan setiap perubahan yang dibuat direkodkan dalam log aktiviti atas nama anda.',
      t2: 'Berikan butiran: namakan kenalan, saluran atau halaman (“di WhatsApp”, “di cawangan Bangsar”).',
      t3: 'Tanya soalan “bagaimana saya …” bila-bila masa — ia menjawab berdasarkan manual ini dan pangkalan pengetahuan.',
    },
    trouble:
      'Lencana “Kotak pasir” muncul di atas: tiada model AI disambungkan, jadi ia hanya memahami permintaan ringkas dan soalan cara — pentadbir menambah kunci di Tetapan → Kunci & model AI. Kad menunjukkan “Tamat tempoh”: tanya sekali lagi. “Saya tidak dapat menghubungi pembantu”: semak sambungan anda dan cuba lagi.',
  },
  myAccount: {
    title: 'Tukar nama atau kata laluan anda',
    summary: 'Tetapkan nama yang dilihat rakan sekerja, tambah e-mel anda, tukar kata laluan dan log keluar peranti yang tidak lagi digunakan — semuanya di Tetapan → Akaun saya.',
    steps: {
      s1: {
        title: 'Buka Tetapan → Akaun saya',
        body: 'Klik nama anda di bahagian bawah bar sisi, atau klik “Tetapan” kemudian “Akaun saya” di bawah “Peribadi”.',
      },
      s2: {
        title: 'Tukar nama anda',
        body: 'Dalam kad “Profil”, taip nama pertama dan nama akhir anda. Tambah e-mel jika mahu, kemudian klik “Simpan profil”. Nama anda di bar sisi dikemas kini serta-merta.',
      },
      s3: {
        title: 'Taip kata laluan semasa',
        body: 'Dalam kad “Kata laluan”, taip kata laluan yang anda gunakan sekarang dalam “Kata laluan semasa”.',
      },
      s4: {
        title: 'Pilih kata laluan baharu',
        body: 'Taip kata laluan baharu dalam “Kata laluan baharu” dan sekali lagi dalam “Sahkan kata laluan baharu”. Ia memerlukan sekurang-kurangnya 8 aksara, dan tidak boleh nama pengguna anda atau kata laluan yang terkenal; petunjuk kekuatan di bawahnya menunjukkan kekuatannya. Klik ikon mata untuk menyemak apa yang ditaip.',
      },
      s5: {
        title: 'Klik “Tukar kata laluan”',
        body: 'Anda melihat “Kata laluan ditukar — N peranti lain dilog keluar”. Pelayar ini kekal log masuk; di tempat lain anda log masuk semula dengan kata laluan baharu.',
      },
      s6: {
        title: 'Log keluar peranti lain (pilihan)',
        body: 'Dalam kad “Peranti”, klik “Log keluar peranti lain” untuk menamatkan setiap sesi lain tanpa menukar kata laluan — contohnya selepas menggunakan komputer kongsi.',
      },
    },
    done: 'Bar sisi menunjukkan nama baharu anda, dan log masuk di tempat lain memerlukan kata laluan baharu.',
    tips: {
      t1: 'Nama pengguna anda tidak pernah berubah — itulah yang anda gunakan untuk log masuk dan yang direkodkan dalam log aktiviti.',
      t2: 'Lupa kata laluan? Hanya pentadbir boleh menetapkan yang baharu untuk anda di halaman Pengguna.',
    },
    trouble: '“Kata laluan semasa anda salah”: semak Caps Lock dan cuba lagi. “Terlalu banyak kata laluan salah”: tunggu kira-kira 15 minit sebelum mencuba lagi. “Pengguna lain sudah mempunyai alamat e-mel itu”: gunakan e-mel lain atau tanya pentadbir.',
  },
  myConnections: {
    title: 'Sambungkan Kalendar Google anda sendiri',
    summary: 'Pautkan Kalendar Google anda kepada ChatBranch supaya AI boleh menyemak bila anda lapang dan menempah janji temu pelanggan terus ke dalam kalendar anda untuk sembang yang ditugaskan kepada anda.',
    before:
      'Anda perlukan akaun Google dengan Kalendar Google. Sesiapa sahaja boleh menyambungkan akaun sendiri — tiada kebenaran khas diperlukan — tetapi pentadbir super mesti menyediakan log masuk Google sekali dahulu (Tetapan → Kunci aplikasi & penyedia). Jika belum, halaman akan memaklumkannya.',
    steps: {
      s1: {
        title: 'Buka Tetapan → Sambungan saya',
        body: 'Klik “Tetapan” di bahagian bawah bar sisi, kemudian “Sambungan saya” di bawah “Peribadi”. Kad itu bertajuk “Akaun Google saya”, bertanda “Anda sahaja”.',
      },
      s2: {
        title: 'Cari Google Calendar',
        body: 'Setiap produk Google mempunyai baris dengan ayat ringkas tentang kegunaannya. Cari baris “Google Calendar”.',
      },
      s3: {
        title: 'Klik “Sambung”',
        body: 'Klik “Sambung” pada baris Google Calendar. Pelayar anda dibawa ke halaman log masuk Google.',
      },
      s4: {
        title: 'Pilih akaun Google anda',
        body: 'Pilih akaun Google yang kalendarnya anda gunakan untuk kerja (log masuk jika diminta).',
      },
      s5: {
        title: 'Benarkan akses',
        body: 'Google menyenaraikan apa yang ChatBranch boleh lakukan dengan kalendar anda. Klik “Teruskan” atau “Benarkan”. Jika anda klik Batal, tiada apa yang disambungkan.',
      },
      s6: {
        title: 'Semak ia disambungkan',
        body: 'Anda kembali ke Sambungan saya dengan mesej “Google Calendar anda disambungkan.” dan label “Disambungkan” pada baris itu.',
      },
      s7: {
        title: 'Putuskan atau tukar akaun kemudian',
        body: 'Klik “Putuskan sambungan” pada baris itu untuk menyahpautnya bila-bila masa, atau “Sambung semula” untuk log masuk sekali lagi (contohnya dengan akaun Google lain).',
      },
    },
    done: 'Baris Google Calendar menunjukkan “Disambungkan”. Kemahiran tempahan yang ditetapkan kepada ejen yang ditugaskan untuk perbualan kini menyemak masa lapang anda dan menempah ke dalam kalendar ini.',
    tips: {
      t1: 'Sambungan anda milik anda seorang — orang lain terus menggunakan sambungan mereka sendiri, atau sambungan kongsi yang disediakan pentadbir.',
      t2: 'Minta pentadbir menetapkan kalendar kemahiran tempahan kepada “Ejen yang ditugaskan untuk perbualan” (Pustaka → Kemahiran AI), supaya tempahan masuk ke kalendar sesiapa yang mengendalikan sembang itu.',
      t3: 'Tempahan yang dibuat oleh AI muncul di halaman Tempahan (Kerja → Tempahan) dan sebagai acara dalam Kalendar Google anda.',
    },
    trouble:
      'Halaman mengatakan log masuk Google belum disediakan untuk ruang kerja ini: pentadbir super perlu mendaftarkan klien Google di Tetapan → Kunci aplikasi & penyedia. “Log masuk Google dibatalkan — tiada apa yang disambungkan.”: klik “Sambung” sekali lagi dan benarkan akses. Baris menunjukkan “Perlu disambung semula”: Google menarik balik akses (contohnya selepas kata laluan ditukar) — klik “Sambung semula”.',
  },
  twoStepSignIn: {
    title: 'Hidupkan log masuk dua langkah',
    summary: 'Lindungi akaun anda dengan kod daripada aplikasi pengesah di telefon anda, supaya kata laluan yang dicuri sahaja tidak boleh masuk.',
    before: 'Pasang aplikasi pengesah di telefon anda — Google Authenticator, Microsoft Authenticator, 1Password atau mana-mana yang lain.',
    steps: {
      s1: {
        title: 'Buka Tetapan → Akaun saya',
        body: 'Klik nama anda di bahagian bawah bar sisi, kemudian cari kad “Log masuk dua langkah”.',
      },
      s2: {
        title: 'Klik “Sediakan log masuk dua langkah”',
        body: 'Kod QR dipaparkan, dengan satu kunci di sebelahnya.',
      },
      s3: {
        title: 'Imbas kod QR',
        body: 'Dalam aplikasi pengesah anda, tambah akaun dan imbas kod tersebut. Tidak dapat mengimbas? Pilih untuk menaip kunci dan salin kunci yang dipaparkan di sebelah kod QR.',
      },
      s4: {
        title: 'Masukkan kod 6 digit',
        body: 'Taip kod yang ditunjukkan aplikasi di bawah “2. Masukkan kod 6 digit yang ditunjukkan aplikasi” dan klik “Hidupkan”.',
      },
      s5: {
        title: 'Simpan kod pemulihan anda',
        body: 'Sepuluh kod pemulihan dipaparkan — setiap satu membolehkan anda log masuk sekali jika telefon hilang. Klik “Muat turun” atau “Salin semua”, simpan di tempat selamat, kemudian klik “Saya sudah menyimpannya”.',
      },
      s6: {
        title: 'Log masuk dengan kod mulai sekarang',
        body: 'Kali seterusnya anda log masuk, ChatBranch meminta kata laluan dan kemudian kod yang ditunjukkan aplikasi. Tiada telefon? Klik “Telefon hilang? Gunakan kod pemulihan”.',
      },
    },
    done: 'Kad itu menunjukkan “Hidup”, bersama bilangan kod pemulihan yang berbaki.',
    tips: {
      t1: 'Telefon baharu? Klik “Pindah ke telefon baharu”, taip kata laluan anda dan imbas kod QR baharu — telefon lama berhenti berfungsi sebaik sahaja anda mengesahkan.',
      t2: 'Hampir habis kod pemulihan? Klik “Kod pemulihan baharu” — kod lama tidak lagi berfungsi.',
    },
    trouble: '“Kod itu tidak betul”: pastikan telefon anda menetapkan jam secara automatik dan taip kod terbaharu (setiap kod hanya berfungsi sekali). Terkunci tanpa telefon atau kod pemulihan: minta pentadbir menetapkan semula log masuk dua langkah anda di halaman Pengguna.',
  },
}
