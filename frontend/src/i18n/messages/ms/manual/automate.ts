import type { automate as source } from '../../en/manual/automate'
import type { Dict } from '../../../types'

export const automate: Dict<typeof source> = {
  journeyFromTemplate: {
    title: 'Cipta perjalanan pertama anda daripada templat',
    summary:
      'Perjalanan menghantar mesej yang betul pada masa yang betul secara automatik — alu-aluan, peringatan, susulan. Bermula daripada templat siap sedia mengambil kira-kira lima minit.',
    before:
      'Anda perlukan log masuk pentadbir (perjalanan memerlukan kebenaran “urus aliran”) dan sekurang-kurangnya satu saluran yang disambungkan, seperti nombor WhatsApp atau peti mel e-mel, supaya mesej ada tempat untuk dihantar.',
    steps: {
      s1: {
        title: 'Buka Perjalanan',
        body: 'Di bar sisi, buka Automasi → Perjalanan. Jika anda belum ada perjalanan, anda akan nampak “Biar susulan berjalan dengan sendiri” dengan kad templat di bawahnya.',
      },
      s2: {
        title: 'Buka galeri templat',
        body: 'Jika anda sudah ada perjalanan, klik “Perjalanan baharu” di bahagian atas kanan. Tetingkap “Mulakan perjalanan baharu” dibuka dengan kad yang sama.',
      },
      s3: {
        title: 'Pilih templat',
        body: 'Setiap kad menerangkan fungsinya dan menunjukkan langkahnya dalam satu baris. Untuk percubaan pertama, klik “Siri alu-aluan” — ia menyapa setiap kenalan baharu dan menghantar e-mel panduan dua hari kemudian.',
      },
      s4: {
        title: 'Baca langkah dalam bahasa mudah',
        body: 'Perjalanan dibuka dalam paparan “Langkah”. Setiap kad ialah satu langkah, dibaca dari atas ke bawah: kad oren di atas menyatakan bila ia bermula (“Apabila…”), kad di bawah menyatakan apa yang berlaku seterusnya.',
      },
      s5: {
        title: 'Ubah ayat supaya berbunyi seperti anda',
        body: 'Klik kad mesej. Panel “Tetapan langkah” dibuka di sebelah kanan; sunting teks dalam “Mesej”. “Perubahan disimpan secara automatik.” — tiada butang simpan.',
      },
      s6: {
        title: 'Betulkan apa-apa yang bertanda ambar',
        body: 'Baris ambar di bawah pengepala (contohnya “2 perkara perlu diselesaikan sebelum perjalanan ini berjalan dengan betul”) dan nota ambar pada kad menunjukkan apa yang tiada, seperti templat atau tag. Klik kad itu dan isi medan yang tiada.',
      },
      s7: {
        title: 'Namakan semula perjalanan (pilihan)',
        body: 'Klik ikon pensel di sebelah nama perjalanan di bahagian atas, taip nama seperti “Alu-aluan — pelanggan baharu” dan klik “Simpan”.',
      },
      s8: {
        title: 'Aktifkan',
        body: 'Setiap templat bermula dalam keadaan dijeda, jadi tiada apa yang dihantar semasa anda menyunting. Apabila berpuas hati, klik “Aktifkan” di bahagian atas kanan. Jika masih ada langkah yang belum siap, anda akan ditanya “Aktifkan dengan langkah yang belum siap?” — pilih “Siapkan dahulu” untuk membetulkannya.',
      },
      s9: {
        title: 'Pastikan ia berjalan',
        body: 'Butang bertukar hijau dan tertulis “Aktif” dengan titik berdenyut, dan perjalanan menunjukkan lencana “AKTIF” dalam senarai perjalanan. Klik “Aktif” sekali lagi pada bila-bila masa untuk menjedanya.',
      },
    },
    done: 'Butang hijau “Aktif” menyala, dan selepas kenalan pertama masuk, kad langkah mula menunjukkan “1 dicapai”.',
    tips: {
      t1: 'Perjalanan “Kenalan dicipta” hanya menyapa kenalan yang dicipta selepas anda mengaktifkannya, jadi menghidupkannya tidak akan menghantar mesej kepada semua kenalan anda.',
      t2: 'Gunakan “Tindakan lain” (butang …) → “Pendua” untuk mencuba perubahan pada salinan sementara yang asal terus berjalan.',
      t3: 'Seterusnya, baca “Tambah, alih dan padam langkah” untuk menyesuaikan templat dengan perniagaan anda.',
    },
    trouble:
      'Tiada apa yang dihantar: pastikan perjalanan “Aktif”, saluran dalam langkah mesej disambungkan di Saluran, dan jenis mesej WhatsApp sesuai (teks bebas hanya sampai kepada orang yang menghantar mesej kepada anda dalam 24 jam lalu — gunakan templat jika tidak). Tidak nampak Perjalanan di bar sisi: peranan anda tiada kebenaran perjalanan; minta bantuan pentadbir.',
  },
  journeyEdit: {
    title: 'Tambah, alih dan padam langkah dalam perjalanan',
    summary:
      'Sesuaikan perjalanan dengan perniagaan anda: hantar mesej, tunggu, buat susulan apabila tiada jawapan, biar AI menulis, kemas kini kenalan, serahkan kepada pasukan dan tamatkan — semuanya daripada paparan Langkah yang mudah.',
    steps: {
      s1: {
        title: 'Buka perjalanan',
        body: 'Di bar sisi, buka Automasi → Perjalanan dan pilih perjalanan daripada senarai “Perjalanan” di bahagian atas. Pastikan “Langkah” (bukan “Kanvas lanjutan”) dipilih.',
      },
      s2: {
        title: 'Klik + untuk menambah langkah',
        body: 'Di antara kad terdapat butang + kecil (“Tambah langkah di sini”). Klik butang di tempat langkah baharu patut berada. Tetingkap “Tambah langkah” dibuka — langkah diletakkan tepat di tempat anda klik.',
      },
      s3: {
        title: 'Hantar mesej',
        body: 'Pilih “Hantar mesej”. Dalam panel di sebelah kanan, pilih saluran di bawah “Hantar melalui” (contohnya WhatsApp), kemudian taip teks dalam “Mesej”. Gunakan “Sisipkan pemboleh ubah” untuk memasukkan nama pertama pelanggan.',
      },
      s4: {
        title: 'Pilih jenis mesej WhatsApp',
        body: 'Untuk WhatsApp, “Jenis mesej WhatsApp” menawarkan “Teks bebas” (hanya dalam 24 jam selepas mesej terakhir mereka), “Templat” (berfungsi bila-bila masa) atau “Teks, jika tidak templat”. Untuk mesej berjadual atau peringatan, pilih templat.',
      },
      s5: {
        title: 'Tunggu',
        body: 'Tambah langkah “Tunggu”. Di bawah “Tunggu”, pilih “Tempoh tetap” (contohnya 2 hari) atau “Balasan”; dengan “Balasan” anda boleh tetapkan “Berhenti selepas” supaya perjalanan tidak menunggu selama-lamanya.',
      },
      s6: {
        title: 'Susulan jika tiada balasan',
        body: 'Tambah “Susulan jika tiada balasan”. Tetapkan “Tunggu sebelum setiap susulan” dan “Susulan paling banyak” (sehingga 5 kali), kemudian tulis “Mesej susulan” atau pilih “AI yang tulis”. Langkah ini berpecah kepada dua laluan: “Dibalas” dan “Tiada balasan”.',
      },
      s7: {
        title: 'Biar AI menulis mesej',
        body: 'Tambah “Mesej AI”. Pilih “Kemahiran” jika mahu, kemudian terangkan matlamatnya dalam “Apa yang patut dilakukan oleh mesej ini?” — contohnya “Ucapkan terima kasih kerana mendaftar dan tanya apa yang membawa mereka kepada kami.” AI menulis ayat dalam bahasa kenalan.',
      },
      s8: {
        title: 'Kemas kini kenalan atau serahkan',
        body: 'Tambah “Kemas kini kenalan” dan pilih “Tambah tag”, “Buang tag” atau “Tetapkan medan” (contohnya tambah tag “qualified”). Atau tambah “Serahkan”, pilih pasukan di bawah “Serahkan kepada” dan tulis “Sebab — nota untuk pasukan”.',
      },
      s9: {
        title: 'Tamatkan dengan Tamat',
        body: 'Di hujung laluan (di tempat tertulis “Laluan tamat”), klik + dan pilih “Tamat”. Taip kod “Hasil” yang ringkas seperti BOOKED atau NO_REPLY supaya anda boleh lihat bagaimana setiap larian berakhir.',
      },
      s10: {
        title: 'Alih atau padam langkah',
        body: 'Halakan tetikus ke atas kad untuk memaparkan butang kecil di bahagian atas kanan: anak panah (“Alih ke atas” / “Alih ke bawah”) dan tong sampah (“Padam langkah”). Memadam langkah yang ada dua laluan akan meminta pengesahan dan mengekalkan laluan YA (atau “Dibalas”).',
      },
      s11: {
        title: 'Gunakan Kanvas lanjutan (pilihan)',
        body: 'Klik “Kanvas lanjutan” untuk melihat perjalanan yang sama sebagai kotak dan garisan. Anda boleh menyeret langkah daripada “Seret masuk langkah”, menyambung kotak dengan menyeret antara titiknya, dan mengemaskan susunan dengan “Susun automatik”. Kembali ke “Langkah” pada bila-bila masa.',
      },
    },
    done: 'Kad baharu muncul mengikut susunan yang betul dalam paparan Langkah, dan tiada kad menunjukkan nota ambar.',
    tips: {
      t1: 'Setiap perubahan disimpan dengan sendiri — pengepala menunjukkan sama ada ia disimpan ke pelayan atau hanya dalam pelayar ini.',
      t2: 'Hidupkan “Hantar seperti manusia” pada langkah mesej untuk jeda menaip yang singkat dan teks panjang dipecahkan kepada beberapa mesej.',
      t3: 'Jeda perjalanan (klik “Aktif”) sebelum perubahan besar, supaya tiada sesiapa menerima versi yang separuh siap.',
      t4: 'Langkah “Hidupkan/matikan chatbot” menghidupkan atau mematikan AI untuk sembang kenalan itu — berguna selepas serahan.',
    },
    trouble:
      '“Tamat” tidak boleh dipilih: Tamat hanya boleh diletakkan di tempat laluan sudah berhenti — klik + di bawah “Laluan tamat”. Ada kad di bawah “Tidak bersambung”: tiada laluan sampai kepadanya, jadi ia tidak akan berjalan; padamkannya atau sambungkannya dalam Kanvas lanjutan. Perubahan hilang: jika pengepala menunjukkan “Luar talian — disimpan dalam pelayar ini”, pelayan tidak dapat dihubungi; muat semula apabila sambungan pulih.',
  },
  journeyTriggers: {
    title: 'Pilih bila perjalanan bermula',
    summary:
      'Kad pertama setiap perjalanan (“Apabila…”) menentukan siapa yang masuk dan bila: kenalan baharu, tag, mesej masuk, tarikh, jadual, atau anda memulakannya secara manual.',
    steps: {
      s1: {
        title: 'Buka kad permulaan',
        body: 'Di Automasi → Perjalanan, buka perjalanan anda dan klik kad paling atas (yang berwarna oren). Panel “Tetapan langkah” dibuka di sebelah kanan.',
      },
      s2: {
        title: 'Pilih apa yang memulakannya',
        body: 'Klik senarai di bawah “Mulakan perjalanan ini apabila”. Pilihannya ialah “Kenalan dicipta”, “Kenalan ditag”, “Mesej diterima”, “Tiba pada suatu tarikh”, “Mengikut jadual” dan “Dimulakan secara manual atau melalui API”.',
      },
      s3: {
        title: 'Kenalan dicipta',
        body: 'Pilih ini untuk mesej alu-aluan. Hanya kenalan yang dicipta selepas perjalanan diaktifkan akan masuk, dari mana-mana sumber — ditaip, diimport atau sembang baharu.',
      },
      s4: {
        title: 'Kenalan ditag',
        body: 'Taip tag di bawah “Tag”, contohnya “Pelanggan baharu”. Setiap kenalan masuk sebaik sahaja tag itu ada pada mereka.',
      },
      s5: {
        title: 'Mesej diterima',
        body: 'Pilih saluran di bawah “Di” (atau “Mana-mana saluran”). Untuk bertindak hanya pada perkataan tertentu, isi “Hanya apabila ia mengandungi (pilihan)”, contohnya harga, sebut harga, tempahan — mana-mana satu perkataan sudah padan.',
      },
      s6: {
        title: 'Tiba pada suatu tarikh',
        body: 'Pilih medan tersuai jenis tarikh di bawah “Medan tarikh” (contohnya hari jadi atau tarikh pembaharuan), kemudian bilangan hari dan “hari sebelum tarikh”, “pada tarikh itu” atau “hari selepas tarikh”. Tandakan “Setiap tahun” untuk hari jadi dan ulang tahun.',
      },
      s7: {
        title: 'Mengikut jadual',
        body: 'Tetapkan “Masa” (zon waktu organisasi anda) dan, di bawah “Pada hari-hari ini”, hari dalam minggu — biarkan kosong untuk setiap hari. Jadual dengan “Semua” berjalan untuk setiap kenalan, jadi hadkan dalam langkah seterusnya.',
      },
      s8: {
        title: 'Tentukan siapa boleh masuk',
        body: 'Di bawah “Siapa boleh masuk”, pilih “Semua”, “Bertag” (taip tag, hanya kenalan itu yang masuk) atau “Dipilih” (cari dan pilih kenalan mengikut nama atau telefon).',
      },
      s9: {
        title: 'Tetapkan peraturan ulangan',
        body: 'Jika dipaparkan, “Jika ia berlaku lagi” menentukan sama ada kenalan boleh masuk lebih daripada sekali: “Sekali bagi setiap kenalan”, “Setiap kali ia berlaku” atau “Paling banyak sekali sehari”.',
      },
      s10: {
        title: 'Dimulakan secara manual atau melalui API',
        body: 'Pilih ini untuk memulakan perjalanan sendiri. Pastikan perjalanan aktif, kemudian di bawah “Mulakan sekarang untuk…” cari dan pilih kenalan dan klik “Mula”. Pembangun anda boleh memulakannya daripada sistem lain melalui alamat API yang dipaparkan di situ.',
      },
    },
    done: 'Kad atas kini menerangkan permulaan dalam bahasa mudah, contohnya “Mesej diterima di WhatsApp yang mengandungi “harga””, tanpa nota ambar.',
    tips: {
      t1: '“Mesej diterima” bermula setiap kali secara lalai — pilih “Paling banyak sekali sehari” supaya orang yang sama tidak dijawab dua kali dalam sembang yang sibuk.',
      t2: 'Tiada medan tarikh untuk dipilih? Tambah satu dahulu di Tetapan → Medan tersuai (jenis Tarikh).',
      t3: 'Mahu cara masuk kedua? Tambah satu lagi kad “Apabila…” dalam Kanvas lanjutan — setiap satu ialah titik permulaan yang berasingan.',
    },
    trouble:
      'Kenalan yang sudah membawa tag itu juga akan masuk apabila perjalanan “Kenalan ditag” diaktifkan — gunakan “Dipilih” atau tag baharu jika anda hanya mahu kenalan baharu. Jadual dihantar kepada semua orang: tukar “Siapa boleh masuk” daripada “Semua” kepada “Bertag” atau “Dipilih”. “Mula” memberi ralat: perjalanan mesti disimpan dan aktif dahulu.',
  },
  journeyDecisions: {
    title: 'Bahagikan perjalanan: semak syarat atau biar AI tentukan',
    summary:
      'Hantar orang yang berbeza ke laluan yang berbeza — contohnya, pelanggan yang kata ya menerima ucapan terima kasih dan yang lain sampai kepada pasukan anda. Anda boleh menetapkan peraturan sendiri, atau biar AI membaca sembang dan membuat keputusan.',
    steps: {
      s1: {
        title: 'Tambah langkah keputusan',
        body: 'Dalam paparan Langkah, klik + di tempat laluan patut berpecah. Pilih “Semak syarat” untuk menetapkan peraturan sendiri, atau “Biar AI tentukan” supaya AI membaca perbualan.',
      },
      s2: {
        title: 'Pilih cara membuat keputusan',
        body: 'Dalam panel di sebelah kanan, “Cara membuat keputusan” ada tiga pilihan: “Semak syarat” (peraturan anda), “AI ya / tidak” (satu soalan, YA / TIDAK) dan “AI pilih laluan” (satu daripada laluan anda). Anda boleh menukarnya bila-bila masa.',
      },
      s3: {
        title: 'Semak syarat: tambah peraturan',
        body: 'Klik “Tambah syarat” dan pilih apa yang hendak disemak — tag, medan tersuai, kenalan, perbualan, balasan mereka atau tempahan. Contohnya “Balasan mereka” → “Mereka kata ya”, atau “Tag” → “Ada tag” → vip.',
      },
      s4: {
        title: 'Semua atau mana-mana',
        body: 'Dengan lebih daripada satu syarat, “Pergi ke YA apabila” menentukan: “Semua ini” (setiap syarat mesti dipenuhi) atau “Mana-mana” (satu sudah cukup). Yang lain pergi ke TIDAK. Syarat disemak serta-merta — tiada apa yang menunggu.',
      },
      s5: {
        title: 'AI ya / tidak: tulis soalan',
        body: 'Di bawah “Soalan untuk AI”, tanya dalam bahasa mudah, contohnya “Adakah pelanggan bersedia menempah lawatan?”. AI menjawab YA atau TIDAK berdasarkan perbualan, atau menyatakan ia tidak pasti.',
      },
      s6: {
        title: 'AI pilih laluan: namakan laluan',
        body: 'Di bawah “Laluan”, beri dua hingga enam nama, seperti “Mahu membeli”, “Ada soalan”, “Ada aduan”, setiap satu dengan penerangan ringkas bila ia patut dipilih. Klik “Tambah laluan” untuk lebih banyak, dan jika mahu “+ “Tiada yang sesuai”” untuk apabila tiada yang padan.',
      },
      s7: {
        title: 'Pilih apa yang AI baca (pilihan)',
        body: 'Di bawah “Apa yang AI baca”, tetapkan bilangan “Mesej terakhir” yang dilihat (5 hingga 100) dan dari mana. Tandakan “Hanya sejak mereka memasuki perjalanan ini” untuk mengabaikan sembang lama. Nota peribadi tidak pernah disertakan.',
      },
      s8: {
        title: 'Tentukan apa berlaku apabila AI tidak pasti',
        body: 'Di bawah “Apabila AI tidak pasti”, pilih “Pergi ke TIDAK”, “Pergi ke YA”, “Ambil “Tiada yang sesuai””, “Tunggu balasan seterusnya dan tanya semula” atau “Tamatkan perjalanan”. Pilihan yang sama digunakan jika AI dimatikan atau tidak dapat dihubungi.',
      },
      s9: {
        title: 'Bina setiap laluan',
        body: 'Paparan Langkah kini menunjukkan laluan bersebelahan (YA dan TIDAK, atau satu lajur bagi setiap laluan AI). Klik + di bawah setiap laluan dan tambah langkahnya. Laluan tanpa langkah hanya menamatkan perjalanan.',
      },
      s10: {
        title: 'Semak keputusan kemudian',
        body: 'Selepas perjalanan aktif, buka “Larian” → “Setiap kenalan” dan klik seseorang: langkah keputusan menunjukkan apa yang diputuskan, contohnya “AI memutuskan YA (0.82)”.',
      },
    },
    done: 'Kad keputusan dibaca dalam bahasa mudah (contohnya “AI bertanya: Adakah pelanggan bersedia menempah lawatan?”) dan setiap laluan di bawahnya ada langkahnya sendiri.',
    tips: {
      t1: 'AI hanya membuat keputusan — ia tidak menghantar apa-apa kepada pelanggan dalam langkah ini.',
      t2: 'Penerangan laluan yang ringkas dan jelas (“bertanya tentang harga, stok atau pesanan”) membantu AI memilih dengan lebih tepat.',
      t3: 'Naikkan “Keyakinan minimum” jika AI terlalu kerap meneka; jawapan yang kurang yakin kemudian dikira sebagai tidak pasti.',
    },
    trouble:
      'AI kelihatan meneka dengan kata kunci mudah: tiada model AI disediakan, jadi ia membuat “tekaan sandbox” (ditandakan dalam panel Larian) — tambah kunci AI di Tetapan → Kunci & model AI. Semua orang pergi ke TIDAK: semak “Semua ini” berbanding “Mana-mana”, dan pastikan setiap syarat ada nilai (nota ambar menunjukkan yang tiada).',
  },
  journeyRuns: {
    title: 'Lihat siapa yang melalui perjalanan dan di mana mereka berhenti',
    summary:
      'Panel Larian menjawab “Adakah ia berkesan?”: berapa ramai yang masuk, sejauh mana mereka pergi, apa yang gagal, dan laluan tepat mana-mana kenalan. Anda juga boleh memulakan perjalanan untuk kenalan yang dipilih.',
    steps: {
      s1: {
        title: 'Buka panel Larian',
        body: 'Di Automasi → Perjalanan, buka perjalanan dan klik “Larian” di bahagian atas (nombor di sebelahnya ialah bilangan larian yang dimulakan). Panel “Adakah ia berkesan?” dibuka di sebelah kanan.',
      },
      s2: {
        title: 'Pilih tempoh',
        body: 'Gunakan suis “7 hari” / “30 hari” di pengepala untuk memilih sejauh mana angka ke belakang.',
      },
      s3: {
        title: 'Baca jumlahnya',
        body: 'Pada tab “Gambaran keseluruhan”, bahagian atas menunjukkan berapa yang “Dimulakan”, berapa yang “Sedang menunggu”, dan carta “Orang yang masuk, setiap hari”.',
      },
      s4: {
        title: 'Cari di mana orang tercicir',
        body: 'Di bawah “Setiap langkah”, setiap baris menunjukkan “Dicapai”, “Gugur” dan “Kini”. “Gugur” yang tinggi bermaksud ramai orang tidak sampai dari langkah sebelumnya ke langkah ini (pada cabang: mereka mengikut laluan lain). Orang yang masih menunggu tidak dikira sebagai gugur.',
      },
      s5: {
        title: 'Lihat apa yang tidak kena',
        body: 'Di bawah “Apa yang tidak kena”, kegagalan disenaraikan bersama sebab dan langkahnya — contohnya templat yang tiada atau kenalan yang telah menarik diri. Betulkan langkah itu, dan orang seterusnya akan melaluinya dengan lancar.',
      },
      s6: {
        title: 'Lihat siapa yang mencapai sesuatu langkah',
        body: 'Klik nombor dalam lajur “Dicapai” sesuatu baris (“Lihat siapa yang mencapai langkah ini”). Tab “Setiap kenalan” dibuka, ditapis kepada langkah itu. Mengklik nama langkah pula membuka tetapan langkah itu.',
      },
      s7: {
        title: 'Ikuti laluan seorang kenalan',
        body: 'Di “Setiap kenalan”, taip nama dalam “Cari kenalan…” atau tapis “Semua orang” / “Sedang berjalan”, kemudian klik seseorang. Anda nampak setiap langkah yang dilalui, masanya, balasan terakhir mereka dan, jika gagal, “Sebab gagal”.',
      },
      s8: {
        title: 'Mulakan perjalanan untuk kenalan yang dipilih',
        body: 'Untuk perjalanan yang bermula dengan “Dimulakan secara manual atau melalui API”, klik kad atas, cari kenalan di bawah “Mulakan sekarang untuk…”, pilih mereka dan klik “Mula”. Anda akan nampak “Dimulakan untuk 3 kenalan.” (sesiapa yang sudah berada di dalamnya dilangkau).',
      },
      s9: {
        title: 'Perhatikan angka pada langkah',
        body: 'Tutup panel dengan butang X. Setiap kad dalam paparan Langkah juga menunjukkan kiraan “dicapai”, “menunggu” dan “gagal” bagi tempoh itu.',
      },
    },
    done: 'Panel Larian menunjukkan angka bagi setiap langkah, dan mengklik kenalan menunjukkan laluan mereka langkah demi langkah.',
    tips: {
      t1: 'Belum ada angka? Larian hanya dikira selepas perjalanan disimpan dan aktif.',
      t2: 'Semak “Apa yang tidak kena” sehari selepas anda mengaktifkan — kebanyakan masalah muncul dalam beberapa larian pertama.',
      t3: 'Baris “Susulan” menunjukkan berapa banyak peringatan dihantar dan berapa ramai yang membalas selepasnya.',
    },
    trouble:
      '“Mula” tidak berbuat apa-apa atau menunjukkan ralat: perjalanan mesti aktif, dan pencetusnya mesti “Dimulakan secara manual atau melalui API”. Kenalan tiada dalam senarai: anda hanya nampak kenalan dalam cawangan (entiti) anda sendiri. Banyak larian gagal pada langkah hantar: buka langkah itu dan pastikan salurannya disambungkan dan templat WhatsAppnya diluluskan.',
  },
  automationRule: {
    title: 'Cipta peraturan automasi',
    summary:
      'Peraturan automasi bertindak serta-merta pada perbualan: apabila sesuatu berlaku, jika syarat dipenuhi, maka lakukan sesuatu — contohnya hantar sembang VIP kepada satu pasukan, serahkan apabila seseorang menulis “refund”, atau tanya pelanggan tidak dikenali siapa mereka.',
    before:
      'Anda perlukan log masuk pentadbir dengan kebenaran “urus aliran”. Untuk menugaskan sembang kepada pasukan, cipta pasukan itu dahulu (bar sisi → Pentadbiran → Pasukan). Peraturan adalah untuk tindak balas segera kepada sembang; untuk mesej yang tersebar beberapa hari, gunakan perjalanan.',
    steps: {
      s1: {
        title: 'Buka Automasi',
        body: 'Di bar sisi, buka Automasi → Automasi. Anda nampak peraturan anda, setiap satu dengan apa yang dicetuskannya, syarat dan tindakannya, serta berapa kali ia telah dicetuskan.',
      },
      s2: {
        title: 'Mulakan peraturan baharu',
        body: 'Klik “Tambah peraturan”. Tetingkap “Peraturan automasi baharu” dibuka.',
      },
      s3: {
        title: 'Namakannya',
        body: 'Di bawah “Nama peraturan”, taip fungsinya, contohnya “Sembang VIP ke pasukan VIP”. “Penerangan” adalah pilihan.',
      },
      s4: {
        title: 'Pilih Apabila',
        body: 'Di bawah “Apabila”, pilih peristiwa: “Perbualan bermula”, “Mesej pelanggan diterima”, “Status perbualan berubah” atau “Perbualan ditugaskan”. Untuk kebanyakan peraturan, “Mesej pelanggan diterima” adalah pilihan yang betul.',
      },
      s5: {
        title: 'Pilih di mana ia terpakai (pilihan)',
        body: 'Di bawah “Terpakai kepada”, kekalkan “Seluruh organisasi” atau pilih satu cawangan (entiti) supaya peraturan hanya bertindak pada perbualan cawangan itu.',
      },
      s6: {
        title: 'Tambah syarat Jika',
        body: 'Klik “+ Tambah syarat” dan pilih medan, pengendali dan nilai — contohnya “Tag kenalan” “mengandungi” vip, atau “Teks mesej” “mengandungi” refund. Semua syarat mesti dipenuhi; tanpa syarat, peraturan dicetuskan setiap kali.',
      },
      s7: {
        title: 'Tambah tindakan Kemudian',
        body: 'Peraturan baharu bermula dengan satu tindakan “Tambah label”. Tukarkannya, atau klik “+ Tambah tindakan” untuk lebih banyak: contohnya “Tugaskan kepada pasukan” → pilih pasukan, atau “Serahkan kepada manusia” → taip “Sebab (dipaparkan kepada pasukan)”.',
      },
      s8: {
        title: 'Contoh: tanya pelanggan tidak dikenali siapa mereka',
        body: 'Dengan “Apabila” = “Perbualan bermula” dan syarat “Kenalan dipautkan (yes/no)” “ialah” “Tidak” (pilih daripada senarai), tambah tindakan “Tanya identiti mereka (pelanggan tidak dikenali)” dan pilih “Tanya — tinggalkan nota jika tiada kenalan yang sepadan” atau “Tanya dan cipta kenalan”.',
      },
      s9: {
        title: 'Simpan peraturan',
        body: 'Biarkan “Aktif” bertanda dan klik “Cipta peraturan”. Mesej “Peraturan … dicipta.” muncul dan peraturan dipaparkan di bahagian bawah senarai dengan suis “Hidup”.',
      },
      s10: {
        title: 'Susun peraturan',
        body: 'Peraturan berjalan dari atas ke bawah, dan setiap peraturan yang syaratnya dipenuhi akan berjalan — bukan hanya yang pertama. Nombor pada setiap peraturan menunjukkan kedudukannya, dan peraturan baharu diletakkan di bawah sekali. Untuk mengalihkan peraturan, seretnya, atau gunakan anak panah atas dan bawah di sebelahnya. Jika dua peraturan menugaskan pasukan, yang lebih bawah menentukan keputusan akhir.',
      },
      s11: {
        title: 'Uji',
        body: 'Klik “Uji” pada peraturan. Dalam “Perbualan untuk diuji”, cari nama, nombor atau mesej dan pilih sembang sebenar; jika mahu, taip “Mesej untuk diuji (pilihan)”, contohnya Saya mahu bayaran balik. Klik “Jalankan ujian”: anda melihat “Peraturan ini akan dijalankan” atau “Peraturan ini tidak akan dijalankan”, setiap syarat ditanda atau dipangkah, dan “Perbualan ini: …” dengan nilai sebenar. Tiada apa-apa dihantar atau diubah.',
      },
    },
    done: 'Ujian menunjukkan “Peraturan ini akan dijalankan” untuk sembang yang sepatutnya padan, dan apabila mesej sebenar tiba, kad peraturan menunjukkan “dicetuskan 1×” (atau lebih).',
    tips: {
      t1: 'Klik butang “Hidup” / “Mati” pada peraturan untuk menjedanya tanpa memadamnya.',
      t2: 'Perkataan dipadankan tanpa mengira huruf besar — “Refund” dan “refund” kedua-duanya padan dengan “mengandungi refund”.',
      t3: 'Gabungkan tindakan: “Tambah label” urgent + “Maklumkan pasukan” + “Matikan chatbot” memastikan seseorang melihatnya.',
      t4: 'Klik ikon pensel pada peraturan untuk menyuntingnya; perubahan anda terpakai pada mesej seterusnya.',
      t5: 'Untuk melihatnya secara sebenar, hantar mesej yang padan dari telefon anda sendiri ke nombor yang disambungkan atau sembang laman web anda, kemudian buka sembang itu di Sembang untuk menyemak label, pasukan atau nota.',
      t6: 'Anda boleh menguji peraturan yang dimatikan — keputusan ujian mengingatkan bahawa ia tidak akan berjalan sehingga anda menghidupkannya.',
    },
    trouble:
      'Peraturan tidak pernah dicetuskan: klik “Uji” dengan sembang yang sepatutnya padan — syarat yang dipangkah menunjukkan apa yang tidak dipenuhi. Pastikan juga ia “Hidup”, “Terpakai kepada” merangkumi cawangan sembang itu, dan setiap syarat ada nilai (pengendali yang tidak dikenali tidak padan dengan apa-apa). Ia dicetuskan terlalu kerap: tambah syarat seperti “Mesej pertama” = Ya. Dua peraturan bercanggah: semua peraturan yang padan akan berjalan, dari atas ke bawah — seret satu ke atas yang lain, matikan satu atau ketatkan syaratnya.',
  },
  accountAssignment: {
    title: 'Pilih nombor yang digunakan oleh setiap kenalan',
    summary:
      'Apabila anda ada beberapa nombor WhatsApp atau peti mel, setiap kenalan kekal pada satu daripadanya supaya perbualan berada pada nombor yang sama. Di sini anda melihat siapa pada nombor mana, memindah atau menyemat kenalan, mengagihkannya sama rata, dan menetapkan peraturan penghalaan dalam bahasa mudah.',
    before:
      'Anda perlukan log masuk pentadbir dengan kebenaran “penetapan akaun” dan sekurang-kurangnya dua akaun yang disambungkan pada saluran yang sama (contohnya dua nombor WhatsApp) — dengan satu sahaja, semua orang memang berada padanya.',
    steps: {
      s1: {
        title: 'Buka Penetapan akaun',
        body: 'Di bar sisi, buka Kerja → Penetapan akaun. Di bahagian atas, pilih “Saluran” (WhatsApp, e-mel…).',
      },
      s2: {
        title: 'Baca kad akaun',
        body: 'Setiap akaun penghantar ada kad yang menunjukkan berapa kenalan dihalakan melaluinya dan berapa yang disemat. Untuk WhatsApp, cip menunjukkan peranannya: nombor perniagaan (peringatan) atau nombor perbualan (peribadi).',
      },
      s3: {
        title: 'Cari seorang kenalan',
        body: 'Taip nama atau telefon dalam “Cari kenalan mengikut nama atau telefon…”. Kad yang mengandungi kenalan itu menunjukkan bilangan padanan.',
      },
      s4: {
        title: 'Pindahkan kenalan ke nombor lain',
        body: 'Klik “Urus kenalan” pada kad. Di sebelah kenalan, pilih akaun lain dalam senarai (atau “Automatik” supaya ChatBranch memilih). Satu titik menandakan perubahan yang belum disimpan.',
      },
      s5: {
        title: 'Semat kenalan',
        body: 'Dalam tetingkap yang sama, hidupkan suis semat di sebelah kenalan (“Disemat — penyeimbangan semula tidak akan memindahkannya”). Gunakan ini untuk VIP yang mesti sentiasa dihubungi dari nombor yang sama. Klik “Simpan perubahan”.',
      },
      s6: {
        title: 'Agihkan kenalan sama rata',
        body: 'Klik “Seimbangkan sama rata”. Tetingkap “Seimbangkan sama rata?” menyatakan berapa kenalan yang tidak disemat mungkin dipindahkan; klik “Seimbangkan semula”. Kenalan yang disemat kekal, dan kenalan pada akaun yang masih ada ruang biasanya kekal juga.',
      },
      s7: {
        title: 'Tetapkan kadar balasan AI',
        body: 'Tatal ke “Peraturan penghalaan”. Di bawah “Kadar balasan AI”, kekalkan “Baris gilir” (disyorkan): balasan AI dihantar satu demi satu bagi setiap nombor dengan jeda seperti manusia, yang melindungi nombor peribadi. “Serta-merta” membalas dengan segera.',
      },
      s8: {
        title: 'Tetapkan pengawal mesej sejuk',
        body: 'Di bawah “Pengawal mesej sejuk”, kekalkan “Sekat” (disyorkan): ia menghalang mesej pertama kepada orang yang tidak pernah menghantar mesej kepada anda — punca utama WhatsApp menyekat nombor peribadi. “Amaran” meminta pengesahan; “Benarkan” tetap menghantar atas risiko anda sendiri.',
      },
      s9: {
        title: 'Chatbot dan ubah hala nombor perniagaan',
        body: '“Chatbot nombor perniagaan” menentukan sama ada AI bersembang pada nombor perniagaan (peringatan) anda secara lalai. Jika ia mati, hidupkan “Ubah hala nombor perniagaan” supaya pelanggan yang membalas di situ menerima satu mesej setiap 24 jam yang mengarahkan mereka ke “Nombor perbualan”, “Khidmat pelanggan” atau “Templat anda”. Isi “Kenalan sokongan” jika anda memilih khidmat pelanggan.',
      },
      s10: {
        title: 'Bersedia jika nombor disekat',
        body: 'Di bawah “Pemulihan sekatan”, pilih “Satu klik daripada pemberitahuan” (disyorkan) atau “Automatik”, dan pilih “Templat pemulihan” yang diluluskan untuk memberitahu pelanggan nombor baharu anda. Jika nombor terputus, sepanduk menunjukkan berapa kenalan terjejas bersama butang “Hantar templat pemulihan”.',
      },
      s11: {
        title: 'Simpan peraturan',
        body: 'Klik “Simpan peraturan penghalaan”. Anda akan nampak “Peraturan penghalaan disimpan — berkuat kuasa serta-merta.”',
      },
    },
    done: 'Kad akaun menunjukkan bilangan kenalan yang anda jangkakan, dan “Peraturan penghalaan disimpan — berkuat kuasa serta-merta.” muncul selepas menyimpan.',
    tips: {
      t1: 'Jika nombor terputus atau disekat, kenalannya yang tidak disemat berpindah secara automatik ke nombor yang sihat, dengan nota dalam sembang — anda tidak perlu melakukannya secara manual.',
      t2: 'Tambah perkataan tarik diri anda sendiri (contohnya BATAL) di bawah “Kata kunci tarik diri”; STOP, UNSUBSCRIBE dan BERHENTI sentiasa berfungsi.',
      t3: 'Untuk memindahkan satu sembang sekarang juga, pentadbir juga boleh menukar nombor dari dalam sembang itu.',
    },
    trouble:
      '“Belum ada akaun WhatsApp”: sambungkan nombor dahulu di Saluran. Penyeimbangan menunjukkan “Sudah seimbang — tiada kenalan dipindahkan.”: kenalan sudah diagihkan, atau semuanya disemat. Senarai templat pemulihan kosong: hanya templat WhatsApp yang diluluskan pada nombor perniagaan (Cloud) boleh dipilih — cipta satu di Templat dan tunggu kelulusan Meta.',
  },
}
