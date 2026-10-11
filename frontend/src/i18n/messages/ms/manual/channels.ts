import type { channels as source } from '../../en/manual/channels'
import type { Dict } from '../../../types'

export const channels: Dict<typeof source> = {
  connectFirstChannel: {
    title: 'Sambungkan saluran pertama anda',
    summary:
      'Pautkan tempat pelanggan sudah menghantar mesej kepada anda — WhatsApp, laman web, e-mel atau aplikasi lain — supaya mesej mereka masuk ke ChatBranch dan pasukan anda boleh menjawabnya dalam satu peti masuk.',
    before:
      'Anda memerlukan log masuk pentadbir dengan kebenaran “Urus akaun saluran”. Bagi kebanyakan saluran, anda juga memerlukan akaun pada platform itu (contohnya akaun perniagaan Meta atau bot Telegram). Selagi saluran belum disambungkan, semuanya berjalan dalam mod sandbox: mesej disimpan tetapi tidak pernah dihantar.',
    steps: {
      s1: {
        title: 'Buka halaman Peti masuk',
        body: 'Di bar sisi, buka Saluran → “Saluran”. Halaman ini bernama “Peti masuk” dan menyenaraikan setiap nombor, peti mel dan widget yang dijawab oleh pasukan anda.',
      },
      s2: {
        title: 'Pilih tempat untuk bermula',
        body: 'Mulakan dengan saluran yang paling banyak digunakan pelanggan anda. “Sembang laman web” paling cepat (tiada akaun luar diperlukan); “WhatsApp Business” terbaik untuk kedai yang menjual melalui WhatsApp; “E-mel” sesuai untuk peti mel sokongan.',
      },
      s3: {
        title: 'Klik satu blok saluran',
        body: 'Tatal ke bawah ke “Tambah saluran” dan klik satu blok, contohnya “WhatsApp Business”. Panel persediaan muncul dari kanan dengan empat langkah di atas: “Pilih saluran”, “Sambung”, “Siapa yang menjawab” dan “Uji dan selesai”.',
      },
      s4: {
        title: 'Isikan butiran sambungan',
        body: 'Pada langkah “Sambung”, salin setiap nilai daripada platform ke dalam medannya. Baris kelabu di bawah setiap medan memberitahu di mana untuk mencarinya, dan “Di mana saya boleh mendapatkannya?” membuka panduan ringkas. Klik “Teruskan”.',
      },
      s5: {
        title: 'Tetapkan siapa yang menjawab',
        body: 'Taip “Nama peti masuk” yang mudah dikenali pasukan anda (contohnya “WhatsApp kedai utama”), pilih pasukan jika ada, dan biarkan yang lain seperti asal buat masa ini. Klik “Cipta peti masuk”.',
      },
      s6: {
        title: 'Jalankan ujian',
        body: 'Pada “Uji dan selesai”, klik “Uji sambungan”. Baris hijau bermaksud ChatBranch boleh mencapai akaun itu; baris merah memberitahu apa yang salah. Sesetengah saluran juga memaparkan alamat webhook di sini untuk disalin ke platform — panduan saluran itu menerangkannya.',
      },
      s7: {
        title: 'Selesai',
        body: 'Klik “Selesai”. Anda akan melihat “Peti masuk sedia” bersama peti masuk, saluran, entiti dan pasukan. Klik “Tutup”, atau “Tambah lagi” untuk menyambungkan saluran seterusnya.',
      },
      s8: {
        title: 'Semak kad baharu',
        body: 'Peti masuk anda kini muncul di bawah “Disambungkan” sebagai kad dengan label kesihatan (contohnya “Sihat”) serta butang “Tetapan” dan “Uji”.',
      },
      s9: {
        title: 'Hantar mesej kepada diri sendiri',
        body: 'Daripada telefon atau e-mel anda sendiri, hantar mesej ke saluran yang baru disambungkan. Di bar sisi, buka Kerja → “Sembang”: mesej itu muncul di bahagian atas senarai dalam beberapa saat.',
      },
    },
    done: 'Kad peti masuk dipaparkan di bawah “Disambungkan” dan mesej ujian anda muncul dalam Sembang.',
    tips: {
      t1: 'Klik “Tetapan” pada kad bila-bila masa untuk menukar siapa yang menjawab atau menggantikan token yang tamat tempoh — medan rahsia yang dibiarkan kosong mengekalkan nilai semasa.',
      t2: 'Gunakan cip saluran di atas kad untuk memaparkan satu jenis peti masuk sahaja apabila senarai semakin panjang.',
      t3: 'Seterusnya, baca “Siapa yang menjawab” untuk menentukan sama ada AI membalas pada peti masuk ini dan pasukan mana yang menerima sembang baharu.',
    },
    trouble:
      'Blok berwarna kelabu: peranan anda tiada kebenaran “Urus akaun saluran” — minta bantuan pentadbir. “Teruskan” memaparkan teks merah: medan wajib kosong atau formatnya salah; baris merah memberitahu medan mana. Ujian lulus tetapi tiada mesej sampai: platform belum dihalakan ke ChatBranch (tampal alamat webhook daripada langkah ujian), atau ChatBranch tidak boleh dicapai pada alamat https awam.',
  },
  whatsappCloud: {
    title: 'Sambungkan nombor WhatsApp Business (Cloud API)',
    summary:
      'Sambungkan nombor WhatsApp Business rasmi anda melalui Cloud API Meta. Ini cara yang selamat dan diluluskan untuk menghantar mesej kepada pelanggan di WhatsApp, dan satu-satunya cara yang boleh menghantar templat yang diluluskan Meta.',
    before:
      'Anda memerlukan: akaun perniagaan Meta (Facebook); aplikasi di developers.facebook.com dengan produk WhatsApp; nombor telefon yang ditambah dan disahkan dalam aplikasi itu; dan ChatBranch yang boleh dicapai pada alamat https awam. Minta pembangun anda membantu bahagian Meta jika ia baharu bagi anda. Pentadbir sahaja.',
    steps: {
      s1: {
        title: 'Salin dua ID daripada Meta',
        body: 'Buka developers.facebook.com → My Apps → aplikasi anda → WhatsApp → API Setup. Salin “Phone number ID” (nombor panjang di bawah nombor telefon anda — bukan nombor telefon itu sendiri) dan “WhatsApp Business Account ID”.',
      },
      s2: {
        title: 'Cipta token akses kekal',
        body: 'Dalam Meta Business Settings → Users → System users, pilih (atau tambah) pengguna sistem dan klik “Generate token” dengan kebenaran whatsapp_business_messaging dan whatsapp_business_management. Salin token itu — token sementara pada halaman API Setup tamat selepas 24 jam.',
      },
      s3: {
        title: 'Salin rahsia aplikasi',
        body: 'Dalam aplikasi anda, buka App settings → Basic dan klik “Show” di sebelah “App secret”. ChatBranch menggunakannya untuk memastikan mesej masuk benar-benar daripada Meta.',
      },
      s4: {
        title: 'Mulakan persediaan dalam ChatBranch',
        body: 'Di bar sisi, buka Saluran → “Saluran”. Di bawah “Tambah saluran” → “Aplikasi pemesejan”, klik “WhatsApp Business”. Panel dibuka pada “Sambungkan nombor”.',
      },
      s5: {
        title: 'Tampal butiran',
        body: 'Tampal “ID nombor telefon”, “ID Akaun WhatsApp Business”, “Token akses” dan “Rahsia aplikasi”. ID akaun ditanda pilihan, tetapi tanpanya anda tidak boleh menyenaraikan atau menghantar templat. Klik “Teruskan”.',
      },
      s6: {
        title: 'Namakan dan pilih siapa yang menjawab',
        body: 'Taip “Nama peti masuk”, contohnya “WhatsApp kedai”, dan pilih pasukan. “Peranan akaun” bermula pada “Peringatan (perniagaan)”, di mana AI dimatikan secara lalai — tetapkan “Balasan AI” kepada “Hidup” jika anda mahu AI bersembang pada nombor ini. Klik “Cipta peti masuk”.',
      },
      s7: {
        title: 'Uji sambungan',
        body: 'Pada “Uji dan selesai”, klik “Uji sambungan”. Baris hijau seperti “Kelayakan tersedia untuk Kedai Anda · +60 12-345 6789.” bermaksud Meta menerima token itu.',
      },
      s8: {
        title: 'Salin butiran webhook',
        body: 'Di bawah “Halakan Meta ke ChatBranch” anda akan melihat “URL panggilan balik” dan “Token pengesahan”, masing-masing dengan butang salin. Salin kedua-duanya.',
      },
      s9: {
        title: 'Tampal ke dalam Meta',
        body: 'Dalam aplikasi Meta anda, buka WhatsApp → Configuration → Webhook → Edit. Tampal URL panggilan balik dan token pengesahan, klik “Verify and save”, kemudian klik “Manage” dan langgan medan “messages”. Anda hanya perlu buat sekali — satu webhook melayani setiap nombor WhatsApp Business.',
      },
      s10: {
        title: 'Selesai dan cuba',
        body: 'Kembali ke ChatBranch dan klik “Selesai”. Hantar mesej WhatsApp daripada telefon anda sendiri ke nombor perniagaan dan buka Kerja → “Sembang” — ia muncul dalam beberapa saat.',
      },
    },
    done: 'Kad memaparkan label hijau “Sihat” dan mesej ujian anda sampai dalam Sembang.',
    tips: {
      t1: 'WhatsApp hanya membenarkan teks bebas dalam masa 24 jam selepas mesej terakhir pelanggan. Selepas itu, hantar templat yang diluluskan daripada sembang.',
      t2: 'Token pengesahan boleh ditukar di Tetapan → “Kunci aplikasi & penyedia”; jika anda menukarnya, tampal yang baharu ke dalam Meta juga.',
      t3: 'Tambah lebih banyak nombor dengan cara yang sama — webhook Meta tidak perlu ditetapkan semula.',
    },
    trouble:
      'Meta mengatakan panggilan balik tidak dapat disahkan: ChatBranch mesti boleh dicapai pada alamat https awam, dan token pengesahan mesti sepadan sepenuhnya. Ujian mengatakan keadaan akaun salah: token tamat tempoh atau tiada kebenaran — jana semula token pengguna sistem dan tampal di “Tetapan”. Mesej sampai ke Meta tetapi tidak ke ChatBranch: pastikan rahsia aplikasi diisi dan anda telah melanggan medan “messages”.',
  },
  whatsappQr: {
    title: 'Pasangkan nombor WhatsApp peribadi melalui kod QR',
    summary:
      'Pautkan nombor WhatsApp biasa ke ChatBranch dengan mengimbas kod QR, sama seperti membuka WhatsApp Web. Ia tidak memerlukan kelulusan Meta — tetapi ia tidak rasmi, jadi gunakannya hanya untuk demo dan ujian.',
    before:
      'Amaran: cara ini menggunakan sambungan WhatsApp Web yang tidak rasmi. Ia melanggar terma WhatsApp dan nombor itu boleh disekat — sentiasa gunakan nombor simpanan, jangan sekali-kali talian perniagaan utama anda. Anda perlu memegang telefon dengan nombor itu, dan pasukan teknikal anda mesti menjalankan gerbang WhatsApp bersama ChatBranch. Pentadbir sahaja.',
    steps: {
      s1: {
        title: 'Buka blok WhatsApp melalui QR',
        body: 'Di bar sisi, buka Saluran → “Saluran”. Di bawah “Tambah saluran” → “Aplikasi pemesejan”, klik “WhatsApp melalui QR” (ia mempunyai lencana “Demo” dan amaran jingga).',
      },
      s2: {
        title: 'Baca amaran dan tandakan kotak',
        body: 'Panel dibuka pada “Sebelum anda memasangkan” dan menerangkan risikonya. Tandakan “Saya faham risikonya dan akan memasangkan nombor simpanan.” dan klik “Teruskan”.',
      },
      s3: {
        title: 'Namakan peti masuk',
        body: 'Taip “Nama peti masuk”, contohnya “WhatsApp demo”. “Peranan akaun” sudah ditetapkan kepada “Perbualan (peribadi)”, yang memastikan penghantaran perlahan seperti manusia untuk melindungi nombor. Pilih pasukan jika mahu.',
      },
      s4: {
        title: 'Cipta peti masuk',
        body: 'Klik “Cipta peti masuk”. Panel beralih ke “Uji dan selesai” dan memaparkan “Pasangkan telefon” dengan kod QR. Kod itu dimuat semula sendiri setiap beberapa saat.',
      },
      s5: {
        title: 'Buka Peranti dipaut pada telefon',
        body: 'Pada telefon, buka WhatsApp → Tetapan → “Peranti dipaut” → “Paut peranti”. Buka kunci telefon jika diminta.',
      },
      s6: {
        title: 'Imbas kod',
        body: 'Halakan kamera telefon ke kod QR pada skrin anda. Selepas seketika, panel memaparkan “Disambungkan sebagai” diikuti nombor itu.',
      },
      s7: {
        title: 'Uji',
        body: 'Klik “Uji sambungan”. Anda sepatutnya melihat “Dipasangkan dan disambungkan” bersama nombor itu.',
      },
      s8: {
        title: 'Selesai',
        body: 'Klik “Selesai”, kemudian “Tutup”. Kad muncul di bawah “Disambungkan” dengan label “Sihat”.',
      },
      s9: {
        title: 'Pastikan telefon sentiasa hidup',
        body: 'Biarkan telefon dicas, disambungkan ke internet dan log masuk ke WhatsApp. Jika telefon lama di luar talian, WhatsApp akan memutuskan pautan ChatBranch dan anda perlu mengimbas semula (klik “Pasangkan” pada kad).',
      },
    },
    done: 'Kad memaparkan “Sihat” dan mesej yang dihantar ke nombor itu muncul dalam Sembang.',
    tips: {
      t1: 'WhatsApp mengehadkan mesej pertama daripada nombor yang dipasangkan kepada orang yang tiada dalam buku alamat telefon. Klik “Eksport kenalan (.vcf)” dan import fail itu ke telefon untuk mengelakkannya.',
      t2: 'Jangan sekali-kali menghantar mesej pukal atau mesej kepada orang yang tidak pernah menghubungi anda daripada nombor yang dipasangkan — itulah cara paling cepat untuk disekat.',
      t3: 'Untuk pelanggan sebenar, sambungkan nombor WhatsApp Business (Cloud API); ia juga boleh menghantar templat.',
    },
    trouble:
      'Tiada kod QR, atau mesej menyebut gerbang: gerbang WhatsApp tidak berjalan — minta pasukan teknikal memulakannya, kemudian buka semula kad dan klik “Pasangkan”. Kad memaparkan “Terputus”: telefon di luar talian atau telah log keluar — klik “Pasangkan” dan imbas semula. Kad memaparkan “Disekat”: WhatsApp menyekat nombor itu; berhenti menggunakannya dan pasangkan nombor simpanan lain.',
  },
  websiteWidget: {
    title: 'Tambah widget sembang ke laman web anda',
    summary:
      'Letakkan gelembung sembang di laman web anda supaya pelawat boleh menghantar mesej tanpa nombor telefon. Sembang mereka masuk ke peti masuk yang sama seperti WhatsApp dan e-mel, dengan AI dan pasukan yang sama.',
    before:
      'Anda memerlukan log masuk pentadbir dan cara untuk menyunting HTML laman web anda (atau kotak “kod tersuai” / “kod footer” dalam pembina laman web anda). Tiada akaun luar diperlukan.',
    steps: {
      s1: {
        title: 'Buka blok Sembang laman web',
        body: 'Di bar sisi, buka Saluran → “Saluran”. Di bawah “Tambah saluran” → “Aplikasi pemesejan”, klik “Sembang laman web”. Panel dibuka pada “Reka bentuk widget”.',
      },
      s2: {
        title: 'Reka rupanya',
        body: 'Secara pilihan, isikan “Tajuk pengepala” (contohnya “Sembang dengan Kedai Roti Ana”), “Ucapan” yang dipaparkan sebelum pelawat menaip, “Warna aksen” gelembung dan “Teks pemegang tempat input”. Medan kosong menggunakan ayat terbina dalam yang mesra.',
      },
      s3: {
        title: 'Pilih bahasa',
        body: 'Di bawah “Bahasa widget”, kekalkan “Automatik — bahasa pelayar pelawat”, atau pilih English, Bahasa Melayu atau 简体中文 untuk sentiasa menggunakan satu bahasa. Klik “Teruskan”.',
      },
      s4: {
        title: 'Namakan dan cipta',
        body: 'Taip “Nama dalaman” seperti “Sembang laman web — laman utama”, pilih pasukan jika ada, dan klik “Cipta peti masuk”.',
      },
      s5: {
        title: 'Salin kod',
        body: 'Pada “Uji dan selesai”, cari “Letakkan pada laman anda”. Klik “Salin” di sebelah kod — ia hanya satu tag skrip.',
      },
      s6: {
        title: 'Tampal ke dalam laman web anda',
        body: 'Buka HTML laman web anda dan tampal kod itu betul-betul sebelum tag penutup </body>, pada setiap halaman yang patut memaparkan gelembung. Dalam pembina laman web, tampalkannya ke kotak footer atau kod tersuai untuk seluruh laman. Terbitkan laman.',
      },
      s7: {
        title: 'Semak bahawa ia menjawab',
        body: 'Kembali ke ChatBranch, klik “Semak widget menjawab”. Baris hijau seperti “Widget menjawab — ia dibuka sebagai “Sembang dengan kami”.” bermaksud widget sudah aktif.',
      },
      s8: {
        title: 'Selesai',
        body: 'Klik “Selesai”, kemudian “Tutup”. Widget muncul di bawah “Disambungkan” bersama bilangan perbualannya.',
      },
      s9: {
        title: 'Cuba sebagai pelawat',
        body: 'Buka laman web anda (tetingkap pelayar peribadi paling sesuai), klik gelembung sembang di penjuru dan hantar mesej. Buka Kerja → “Sembang” — ia muncul di situ.',
      },
    },
    done: 'Gelembung sembang dipaparkan di laman web anda dan mesej ujian anda muncul dalam Sembang.',
    tips: {
      t1: 'Kod ini selamat untuk didedahkan — ia hanya membenarkan pelawat memulakan sembang. Jika ia berada di tempat yang tidak sepatutnya, klik “Tukar token” dan tampal kod baharu.',
      t2: 'Buat satu widget bagi setiap laman web atau jenama, masing-masing dengan nama dan pasukannya sendiri.',
      t3: 'Tukar rupanya kemudian melalui “Tetapan” pada kad — kod di laman anda kekal sama.',
    },
    trouble:
      'Tiada gelembung di laman: pastikan keseluruhan kod ditampal sebelum </body>, halaman telah diterbitkan, dan kosongkan cache pelayar. Semakan mengatakan widget tidak menjawab: ChatBranch tidak boleh dicapai dari internet pada alamat awamnya — tanya pasukan teknikal anda. Selepas “Tukar token” gelembung hilang: tampal kod baharu; kod lama sengaja dihentikan.',
  },
  emailMailbox: {
    title: 'Sambungkan peti mel sokongan',
    summary:
      'Hantar dan terima e-mel dalam ChatBranch daripada peti mel anda sendiri (contohnya support@kedaianda.com). E-mel keluar menggunakan SMTP; balasan pelanggan dibaca semula melalui IMAP.',
    before:
      'Anda memerlukan alamat peti mel dan log masuknya. Untuk Gmail atau Outlook dengan log masuk dua langkah, anda memerlukan kata laluan aplikasi (kata laluan 16 aksara yang dibuat khas untuk ChatBranch) — bukan kata laluan biasa anda. Pentadbir sahaja.',
    steps: {
      s1: {
        title: 'Cipta kata laluan aplikasi',
        body: 'Gmail: Akaun Google → Keselamatan → hidupkan Pengesahan 2 Langkah → “Kata laluan apl”, cipta satu bernama “ChatBranch” dan salin 16 hurufnya. Outlook / Microsoft 365: akaun Microsoft → Keselamatan → “Kata laluan aplikasi” (akaun kerja mungkin memerlukan pentadbir IT membenarkan log masuk SMTP).',
      },
      s2: {
        title: 'Buka blok E-mel',
        body: 'Di bar sisi, buka Saluran → “Saluran”. Di bawah “Tambah saluran” → “SMS, e-mel & suara”, klik “E-mel”. Panel dibuka pada “Sambungkan peti mel”.',
      },
      s3: {
        title: 'Isikan butiran penghantaran',
        body: 'Taip “Alamat penghantar” (support@kedaianda.com), “Hos SMTP” (Gmail smtp.gmail.com, Outlook smtp.office365.com), “Nama pengguna” (biasanya alamat e-mel penuh) dan tampal kata laluan aplikasi dalam “Kata laluan”. “Port” boleh dibiarkan kosong (587 digunakan).',
      },
      s4: {
        title: 'Tambah butiran masuk',
        body: 'Untuk menerima balasan, isikan “Hos IMAP (balasan)” — Gmail imap.gmail.com, Outlook outlook.office365.com. “Port IMAP” boleh dibiarkan kosong (993). Biarkan “Guna TLS” dihidupkan. Klik “Teruskan”.',
      },
      s5: {
        title: 'Namakan dan cipta',
        body: '“Nama peti masuk” diisi dengan alamat itu; tukar jika mahu, pilih pasukan, dan klik “Cipta peti masuk”.',
      },
      s6: {
        title: 'Hantar e-mel ujian',
        body: 'Pada “Uji dan selesai”, taip alamat anda sendiri dalam “Hantar e-mel ujian kepada” dan klik “Uji sambungan”. Anda akan melihat “E-mel ujian dihantar kepada …” — semak peti masuk anda.',
      },
      s7: {
        title: 'Selesai',
        body: 'Klik “Selesai”, kemudian “Tutup”. Peti mel muncul di bawah “Disambungkan”.',
      },
      s8: {
        title: 'Ketahui di mana pengirim baharu muncul',
        body: 'Setiap e-mel yang sampai ke peti mel muncul dalam Sembang — termasuk daripada orang yang belum menjadi kenalan. Sembang itu berada di bawah “Bukan kenalan” di bahagian atas senarai sembang, dan AI tidak membalasnya sehingga anda memautkan atau mencipta kenalan. Balasan automatik (di luar pejabat) tidak dimasukkan. E-mel yang anda hantar dari aplikasi e-mel anda sendiri (Outlook, Gmail…) muncul dalam sembang sebagai balasan anda, balasan yang dihantar dari ChatBranch disimpan dalam folder Sent peti mel, dan ChatBranch tidak pernah menandakan e-mel sebagai sudah dibaca.',
      },
      s9: {
        title: 'Balas e-mel ujian',
        body: 'Balas e-mel ujian daripada alamat itu. Selepas seminit, buka Kerja → “Sembang” — balasan itu muncul sebagai perbualan e-mel yang boleh anda jawab.',
      },
    },
    done: 'E-mel ujian sampai ke peti masuk anda dan balasan anda muncul dalam Sembang.',
    tips: {
      t1: 'Gunakan alamat pasukan yang dikongsi (support@, hello@) dan bukannya peti mel peribadi seseorang.',
      t2: 'Balasan dihantar sebagai bebenang e-mel biasa dengan subjek “Re:”, jadi pelanggan boleh terus membalas daripada aplikasi e-mel mereka sendiri.',
      t3: 'Anda boleh menyambungkan beberapa peti mel; setiap perbualan terus menggunakan peti mel tempat ia bermula.',
    },
    trouble:
      'Log masuk gagal: gunakan kata laluan aplikasi, bukan kata laluan biasa, dan alamat e-mel penuh sebagai nama pengguna. Gmail menyebut “apl kurang selamat”: hidupkan Pengesahan 2 Langkah dahulu, kemudian cipta kata laluan aplikasi. Balasan tidak pernah sampai: pastikan hos IMAP diisi, kemudian lihat di bawah “Bukan kenalan” dalam Sembang.',
  },
  telegramBot: {
    title: 'Sambungkan bot Telegram',
    summary:
      'Cipta bot Telegram untuk perniagaan anda dan sambungkannya, supaya pelanggan yang menghantar mesej kepada bot sampai kepada pasukan anda dalam ChatBranch.',
    before:
      'Anda memerlukan aplikasi Telegram pada telefon atau komputer, log masuk pentadbir, dan ChatBranch yang boleh dicapai pada alamat https awam (pasukan teknikal anda mengetahuinya, contohnya https://chat.kedaianda.com).',
    steps: {
      s1: {
        title: 'Cipta bot dengan BotFather',
        body: 'Dalam Telegram, cari @BotFather (ia mempunyai tanda biru) dan buka sembangnya. Hantar /newbot, kemudian nama paparan (contohnya “Kedai Roti Ana”) dan nama pengguna yang berakhir dengan “bot” (contohnya kedairotiana_bot).',
      },
      s2: {
        title: 'Salin token',
        body: 'BotFather membalas dengan token seperti 123456789:AAH… — salin kesemuanya. Rahsiakan token ini: sesiapa yang memilikinya boleh mengawal bot.',
      },
      s3: {
        title: 'Buka blok Telegram',
        body: 'Dalam ChatBranch, buka Saluran → “Saluran”. Di bawah “Tambah saluran” → “Aplikasi pemesejan”, klik “Telegram”. Tampal token dalam “Token bot” dan klik “Teruskan”.',
      },
      s4: {
        title: 'Namakan dan cipta',
        body: 'Taip “Nama peti masuk”, pilih pasukan jika ada, dan klik “Cipta peti masuk”.',
      },
      s5: {
        title: 'Uji sambungan',
        body: 'Pada “Uji dan selesai”, klik “Uji sambungan”. Anda sepatutnya melihat “Disambungkan sebagai” diikuti nama bot anda.',
      },
      s6: {
        title: 'Daftar webhook',
        body: 'Di bawah “Daftar webhook”, taip alamat https awam ChatBranch dalam “URL asas awam” (contohnya https://chat.kedaianda.com) dan klik “Daftar”. Anda akan melihat “Webhook didaftarkan.” — Telegram kini menghantar mesej ke ChatBranch.',
      },
      s7: {
        title: 'Selesai',
        body: 'Klik “Selesai”, kemudian “Tutup”. Bot muncul di bawah “Disambungkan”.',
      },
      s8: {
        title: 'Hantar mesej kepada bot',
        body: 'Dalam Telegram, buka bot anda (t.me/nama_pengguna_bot), ketik “Start” dan hantar mesej. Buka Kerja → “Sembang” — ia muncul di situ.',
      },
    },
    done: 'Kad memaparkan “Sihat” dan mesej anda kepada bot muncul dalam Sembang.',
    tips: {
      t1: 'Gunakan /setuserpic dan /setdescription BotFather untuk memberi bot logo dan penerangan ringkas.',
      t2: 'Kongsi bot sebagai pautan (t.me/nama_pengguna_bot) di laman web, resit atau profil media sosial anda.',
      t3: 'Hilang token? Hantar /token kepada BotFather, pilih bot, kemudian tampal token baharu dalam “Tetapan” kad.',
    },
    trouble:
      '“Daftar” memaparkan ralat tentang https: taip alamat https awam, bukan localhost. Ujian gagal: token tidak disalin sepenuhnya atau telah diganti dalam BotFather — tampal semula. Mesej tidak sampai: klik “Daftar” sekali lagi selepas sebarang perubahan alamat.',
  },
  messengerInstagram: {
    title: 'Sambungkan halaman Facebook atau akaun Instagram',
    summary:
      'Bawa mesej Facebook Messenger dan mesej terus Instagram ke dalam ChatBranch, supaya pasukan anda menjawabnya bersama WhatsApp dan e-mel.',
    before:
      'Anda memerlukan: halaman Facebook yang anda urus; aplikasi Meta di developers.facebook.com dengan produk Messenger (dan produk Instagram untuk Instagram); untuk Instagram, akaun profesional yang dipautkan ke halaman itu; dan ChatBranch yang boleh dicapai pada alamat https awam. Pentadbir sahaja.',
    steps: {
      s1: {
        title: 'Cari ID halaman atau akaun',
        body: 'ID halaman Facebook: halaman anda → About → Page transparency, atau Meta Business Suite → Settings → Page info. ID akaun Instagram: Meta App Dashboard → Instagram → API setup.',
      },
      s2: {
        title: 'Jana token akses halaman',
        body: 'Dalam Meta App Dashboard, buka Messenger → Settings → Access tokens dan klik “Generate” di sebelah halaman anda. Salin token itu. Instagram menggunakan token halaman yang dipautkan kepadanya.',
      },
      s3: {
        title: 'Salin rahsia aplikasi',
        body: 'Dalam aplikasi anda, buka App settings → Basic dan salin “App secret”. Isikannya — ChatBranch menolak mesej masuk yang tidak ditandatangani Meta dengannya.',
      },
      s4: {
        title: 'Buka blok',
        body: 'Dalam ChatBranch, buka Saluran → “Saluran”. Di bawah “Tambah saluran” → “Media sosial”, klik “Messenger” atau “Instagram”.',
      },
      s5: {
        title: 'Tampal butiran',
        body: 'Tampal “ID Halaman Facebook” (atau “ID akaun Instagram”), “Token akses halaman” dan “Rahsia aplikasi”. Klik “Teruskan”.',
      },
      s6: {
        title: 'Namakan dan cipta',
        body: 'Taip “Nama peti masuk” seperti “Halaman Facebook”, pilih pasukan, dan klik “Cipta peti masuk”. Kemudian klik “Uji sambungan” — baris hijau bermaksud token berfungsi.',
      },
      s7: {
        title: 'Salin butiran webhook',
        body: 'Di bawah “Halakan Meta ke ChatBranch”, salin “URL panggilan balik” dan “Token pengesahan”.',
      },
      s8: {
        title: 'Tampal ke dalam Meta',
        body: 'Dalam aplikasi Meta anda, buka tetapan webhook Messenger (atau Instagram), tampal URL panggilan balik dan token pengesahan, sahkan, dan langgan halaman anda kepada medan “messages”. Kemudian klik “Selesai” dalam ChatBranch.',
      },
      s9: {
        title: 'Fahami tempoh balasan',
        body: 'Dalam masa 24 jam selepas mesej terakhir pelanggan, sesiapa sahaja boleh membalas — AI, aliran atau ejen. Dari 24 jam hingga 7 hari, hanya orang yang menaip dalam Sembang boleh membalas. Selepas 7 hari, tiada sesiapa boleh menghantar mesej sehingga pelanggan menulis semula.',
      },
    },
    done: 'Mesej yang dihantar ke halaman atau akaun Instagram anda muncul dalam Sembang dan anda boleh membalasnya.',
    tips: {
      t1: 'Jawab dengan cepat: selepas 24 jam, AI dan mesej automatik disekat pada saluran ini.',
      t2: 'Gunakan penapis tempoh balasan dalam Sembang untuk mencari perbualan yang hampir ditutup.',
      t3: 'Messenger dan Instagram menggunakan URL panggilan balik dan token pengesahan yang sama seperti WhatsApp Business.',
    },
    trouble:
      'Mesej tidak sampai: rahsia aplikasi tiada atau salah, atau halaman tidak melanggan “messages”. Ujian gagal: token halaman tamat tempoh — jana yang baharu dan tampal di “Tetapan”. Balasan ditolak: pelanggan menulis lebih 24 jam lalu (AI disekat) atau lebih 7 hari lalu (semua disekat).',
  },
  otherChannels: {
    title: 'Sambungkan LINE, Viber, SMS, WeChat, Zalo, TikTok atau Douyin',
    summary:
      'Semua saluran ini disambungkan dengan cara yang sama: salin beberapa kelayakan daripada platform ke ChatBranch, kemudian tampal alamat webhook yang ditunjukkan ChatBranch kembali ke platform.',
    before:
      'Anda memerlukan akaun perniagaan pada platform itu (LINE Official Account, bot Viber, akaun Twilio dengan nombor SMS, WeChat Service Account, Zalo Official Account…) dan ChatBranch yang boleh dicapai pada alamat https awam. TikTok dan Douyin hanya membuka pemesejan kepada aplikasi yang diluluskan — mohon dahulu. Pentadbir sahaja.',
    steps: {
      s1: {
        title: 'Cari blok saluran',
        body: 'Di bar sisi, buka Saluran → “Saluran” dan tatal ke “Tambah saluran”. LINE, Viber dan Zalo berada di bawah “Aplikasi pemesejan”, TikTok di bawah “Media sosial”, SMS di bawah “SMS, e-mel & suara”, WeChat dan Douyin di bawah “China”. Blok bertanda “Perlu kelulusan” memerlukan kelulusan platform dahulu.',
      },
      s2: {
        title: 'Buka panduan ringkas',
        body: 'Klik blok itu. Pada langkah “Sambung”, buka “Di mana saya boleh mendapatkannya?” untuk langkah platform itu sendiri, dan baca baris bantuan kelabu di bawah setiap medan.',
      },
      s3: {
        title: 'Salin kelayakan',
        body: 'Salin setiap nilai daripada platform. Contohnya: LINE — “Token akses saluran” dan “Rahsia saluran”; Viber — “Token pengesahan”; SMS — “Account SID”, “Auth token” dan “Nombor penghantar” seperti +60123456789; WeChat — “AppID”, “AppSecret” dan “Token pelayan” yang anda reka sendiri; Zalo — “ID aplikasi”, “Kunci rahsia OA” dan token-tokennya. Klik “Teruskan”.',
      },
      s4: {
        title: 'Namakan dan cipta',
        body: 'Taip “Nama peti masuk”, pilih pasukan, dan klik “Cipta peti masuk”.',
      },
      s5: {
        title: 'Salin URL webhook',
        body: 'Pada “Uji dan selesai”, cari “Halakan … ke ChatBranch” dan klik “Salin” di sebelah “URL webhook”. Setiap peti masuk mempunyai alamatnya sendiri.',
      },
      s6: {
        title: 'Tampal ke dalam platform',
        body: 'Ikut baris di atas URL. Contohnya, Twilio: nombor anda → Messaging configuration → “A message comes in”, Webhook, HTTP POST. WeChat: Server configuration → URL, Token yang sama, kemudian Enable. Untuk LINE dan Viber, anda juga boleh menaip “URL asas awam” dan klik “Daftar”.',
      },
      s7: {
        title: 'Uji sambungan',
        body: 'Klik “Uji sambungan”. Baris hijau (selalunya “Disambungkan sebagai …”) bermaksud kelayakan berfungsi; baris jingga ialah amaran untuk dibaca, contohnya webhook belum aktif.',
      },
      s8: {
        title: 'Selesai dan hantar ujian',
        body: 'Klik “Selesai”. Hantar mesej ke akaun anda daripada aplikasi di telefon, kemudian buka Kerja → “Sembang” untuk melihatnya.',
      },
    },
    done: 'Kad memaparkan “Sihat” dan mesej daripada aplikasi itu muncul dalam Sembang.',
    tips: {
      t1: 'Sesetengah saluran hanya membenarkan balasan untuk satu tempoh selepas mesej terakhir pelanggan: WeChat, TikTok dan Douyin 48 jam, Zalo 7 hari. Nota di bawah URL webhook menyatakan yang mana terpakai.',
      t2: 'LINE, Viber dan SMS belum boleh menghantar fail daripada ChatBranch — hantar teks atau pautan.',
      t3: 'Apabila pelanggan berhenti mengikuti, berhenti melanggan atau menghantar STOP, perbualan ditanda menarik diri dan tiada lagi mesej dihantar.',
      t4: 'TikTok dan Douyin ditanda “Beta”: ia mengikut peraturan rasmi platform tetapi belum dicuba dengan perkhidmatan sebenar.',
    },
    trouble:
      'Platform mengatakan webhook gagal: ChatBranch mesti berada pada alamat https awam, dan rahsianya (rahsia saluran, auth token, kunci rahsia OA…) mesti diisi — tanpanya mesej masuk ditolak. Ujian WeChat gagal: tambah IP pelayan ChatBranch ke senarai putih IP dalam platform WeChat. TikTok atau Douyin tiada kelayakan: platform belum meluluskan aplikasi anda; peti masuk kekal dalam sandbox sehingga itu.',
  },
  whoAnswers: {
    title: 'Pilih siapa yang menjawab sesuatu peti masuk',
    summary:
      'Tentukan, bagi setiap peti masuk, pasukan mana yang menerima sembang baharu, sama ada AI membalas, sama ada balasan AI disemak dahulu, dan apa yang berlaku apabila orang yang tidak dikenali menulis.',
    before:
      'Peti masuk mesti sudah disambungkan. Anda memerlukan log masuk pentadbir. Untuk menghalakan sembang ke pasukan, cipta pasukan dahulu di Pentadbiran → “Pasukan”.',
    steps: {
      s1: {
        title: 'Buka tetapan peti masuk',
        body: 'Di bar sisi, buka Saluran → “Saluran” dan klik “Tetapan” pada kad peti masuk. Panel dibuka pada “Siapa yang menjawab”.',
      },
      s2: {
        title: 'Semak nama',
        body: '“Nama peti masuk” ialah nama yang dilihat pasukan anda dalam Sembang dan laporan. Jadikannya jelas, contohnya “WhatsApp cawangan KL”.',
      },
      s3: {
        title: 'Pilih entiti',
        body: 'Pilih cawangan atau syarikat yang memiliki peti masuk ini. Setiap perbualan yang tiba di sini difailkan di bawahnya. Biarkan seluruh organisasi jika semua cawangan berkongsi peti masuk ini.',
      },
      s4: {
        title: 'Pilih pasukan',
        body: 'Di bawah “Pasukan”, pilih siapa yang menerima perbualan baharu. Ia diagihkan secara bergilir dalam kalangan ahli pasukan. “Tiada pasukan” menggunakan pasukan tugasan automatik entiti.',
      },
      s5: {
        title: 'Tetapkan peranan akaun (WhatsApp sahaja)',
        body: '“Peringatan (perniagaan)” untuk nombor rasmi yang menghantar templat; AI dimatikan secara lalai. “Perbualan (peribadi)” mengendalikan sembang AI dan menghantar pada kadar perlahan seperti manusia.',
      },
      s6: {
        title: 'Pilih sama ada AI membalas',
        body: 'Di bawah “Balasan AI”, pilih “Hidup” atau “Mati” untuk perbualan baharu. “Lalai” (atau “Ikut peranan” pada WhatsApp) mengikut peraturan biasa. Ejen masih boleh menukar AI bagi setiap sembang, dan pilihan mereka diutamakan.',
      },
      s7: {
        title: 'Pilih sama ada AI disemak dahulu',
        body: 'Di bawah “Semak AI sebelum ia menghantar”, pilih “Hantar secara automatik”, “Tahan balasan berisiko untuk disemak”, “Tahan setiap balasan AI untuk disemak” atau “Bayangan — draf sahaja, tidak pernah dihantar”. “Guna tetapan Pemantau AI” mengikut pilihan organisasi.',
      },
      s8: {
        title: 'Tentukan apa berlaku pada pelanggan tidak dikenali',
        body: 'Di bawah “Pelanggan tidak dikenali”, pilih “Jangan buat apa-apa”, “Tanya siapa mereka” atau “Tanya dan cipta kenalan”. Bertanya menghantar soalan ringkas tentang nama dan telefon atau e-mel orang itu; anda boleh menulis ayat sendiri dalam “Ayat anda sendiri (pilihan)”.',
      },
      s9: {
        title: 'Simpan',
        body: 'Klik “Simpan & teruskan”, kemudian “Selesai”. Kad kini memaparkan pasukan dan “AI hidup” atau “AI mati”.',
      },
    },
    done: 'Kad peti masuk memaparkan pasukan yang anda pilih dan label AI hidup/mati, dan sembang baharu pergi ke pasukan itu.',
    tips: {
      t1: 'Baharu dengan AI? Mulakan dengan “Tahan setiap balasan AI untuk disemak” dan luluskan balasan di Automasi → “Pemantau AI” sehingga anda yakin.',
      t2: 'Ini ialah lalai untuk perbualan BAHARU sahaja — menukarnya tidak menghidupkan atau mematikan AI dalam sembang yang sedia ada.',
      t3: '“Tanya dan cipta kenalan” memastikan senarai kenalan anda lengkap tanpa menaip, tetapi semak kenalan baharu dari semasa ke semasa.',
    },
    trouble:
      'Senarai pasukan mengatakan tiada pasukan: cipta satu di Pentadbiran → “Pasukan”, kemudian kembali. AI masih tidak membalas: suis AI seluruh organisasi pada Pemantau AI mungkin dimatikan, kenalan mungkin ditetapkan untuk tidak sekali-kali menggunakan AI, atau ejen telah mematikannya dalam sembang itu. AI membalas tetapi tiada yang dihantar: mod semakan menahan balasan — luluskan dalam Pemantau AI.',
  },
  channelHealth: {
    title: 'Semak bahawa saluran anda berfungsi',
    summary:
      'Baca label kesihatan pada peti masuk anda, semak bila-bila masa, dan ketahui apa yang perlu dibuat apabila nombor terputus atau disekat.',
    steps: {
      s1: {
        title: 'Lihat label kesihatan',
        body: 'Di bar sisi, buka Saluran → “Saluran”. Setiap kad mempunyai titik berwarna dan label: “Sihat”, “Terjejas”, “Tidak diketahui”, “Terputus” atau “Disekat”. Letakkan tetikus pada label untuk membaca sebabnya.',
      },
      s2: {
        title: 'Fahami label',
        body: '“Sihat” berfungsi. “Terjejas” berfungsi tetapi ada masalah (contohnya webhook tidak aktif). “Tidak diketahui” belum disemak. “Terputus” tidak boleh menghantar — biasanya token tamat tempoh atau telefon tidak dipasangkan. “Disekat” bermaksud platform menyekat akaun itu.',
      },
      s3: {
        title: 'Uji satu peti masuk sekarang',
        body: 'Klik “Uji” pada kad. Keputusan ringkas muncul pada kad: hijau apabila semuanya baik, merah bersama sebabnya jika tidak.',
      },
      s4: {
        title: 'Semak semuanya serentak',
        body: 'Buka “Papan pemuka” (halaman utama). Pada kad “Kesihatan saluran”, klik “Semak sekarang” untuk menyemak setiap akaun; “Urus” membawa anda kembali ke halaman Peti masuk.',
      },
      s5: {
        title: 'Baiki peti masuk yang terputus',
        body: 'Klik “Tetapan” pada kad, pergi ke langkah “Sambung” dan tampal token atau kata laluan baharu (medan kosong mengekalkan yang lama), kemudian simpan. Untuk nombor WhatsApp yang dipasangkan melalui QR, klik “Pasangkan” dan imbas kod sekali lagi.',
      },
      s6: {
        title: 'Uruskan nombor yang disekat',
        body: 'Berhenti menggunakannya — menghantar lebih banyak tidak akan membantu. Sambungkan nombor pengganti, kemudian padam peti masuk yang disekat atau biarkan untuk sejarahnya; perbualannya disimpan.',
      },
      s7: {
        title: 'Fahami fungsi failover',
        body: 'Apabila nombor menjadi “Terputus” atau “Disekat”, ChatBranch memindahkan perbualannya ke nombor lain yang sihat daripada jenis yang sama, menulis nota dalam setiap sembang dan memaklumkan pentadbir melalui loceng. Jika tiada nombor lain, penghantaran gagal dan sebabnya dinyatakan.',
      },
      s8: {
        title: 'Sediakan pemulihan sekatan (pilihan)',
        body: 'Buka Kerja → “Penetapan akaun” → “Peraturan penghalaan”. Di bawah “Pemulihan sekatan”, pilih “Satu klik daripada pemberitahuan” atau “Automatik”, dan pilih “Templat pemulihan” yang memberitahu pelanggan nombor baharu anda.',
      },
    },
    done: 'Setiap kad memaparkan “Sihat”, dan anda tahu di mana untuk melihat apabila ada yang tidak.',
    tips: {
      t1: 'Kesihatan disemak secara automatik setiap beberapa minit, dan pentadbir menerima pemberitahuan loceng apabila akaun terputus atau pulih.',
      t2: 'Sentiasa sambungkan satu nombor perbualan simpanan, supaya failover ada tempat untuk memindahkan sembang.',
      t3: 'Menyimpan kelayakan baharu menetapkan semula label kepada “Tidak diketahui” dan menyemak semula di latar belakang — tunggu seketika atau klik “Uji”.',
    },
    trouble:
      'Kad memaparkan “Tidak dipantau”: peti masuk itu tiada semakan kesihatan langsung (contohnya trunk suara) — gunakan “Uji”. Label kekal “Tidak diketahui”: klik “Semak sekarang” pada Papan pemuka. “Terjejas” pada Telegram, LINE atau Viber: webhook belum didaftarkan — buka “Tetapan”, pergi ke “Uji dan selesai” dan klik “Daftar” sekali lagi.',
  },
}
