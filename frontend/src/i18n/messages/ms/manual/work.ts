import type { work as source } from '../../en/manual/work'
import type { Dict } from '../../../types'

export const work: Dict<typeof source> = {
  replyToChat: {
    title: 'Balas mesej pelanggan',
    summary: 'Buka perbualan, tulis jawapan, tambah fail jika perlu, dan tandakan sembang sebagai selesai apabila pelanggan sudah dibantu.',
    steps: {
      s1: {
        title: 'Buka peti masuk',
        body: 'Di bar sisi, di bawah Kerja, klik “Sembang”. Skrin ada tiga bahagian: senarai perbualan di kiri, perbualan yang dibuka di tengah dan butirannya di kanan (pada skrin lebar). Pada telefon atau tablet, buka butiran dengan butang “Butiran perbualan” (ⓘ) di bahagian atas sembang.',
      },
      s2: {
        title: 'Pilih perbualan',
        body: 'Klik satu baris dalam senarai. Titik kecil berwarna pada baris bermaksud “Menunggu balasan kita”. Mesej dipaparkan di tengah, yang terbaharu di bawah.',
      },
      s3: {
        title: 'Pastikan “Balas” dipilih',
        body: 'Di bawah mesej ada dua tab: “Balas” dan “Nota peribadi”. Kekalkan “Balas” — nota peribadi hanya dilihat oleh pasukan anda, tidak sekali-kali oleh pelanggan.',
      },
      s4: {
        title: 'Taip jawapan anda',
        body: 'Klik kotak “Taip mesej (/ untuk balasan pantas)” dan tulis jawapan anda, contohnya: “Hai Aisyah, pesanan anda sedia untuk diambil hari ini.” Kotak itu membesar semasa anda menaip; tekan Shift+Enter untuk memulakan baris baharu.',
      },
      s5: {
        title: 'Tambah emoji atau jawapan sedia',
        body: 'Klik butang senyum, “Emoji”, di kiri kotak mesej: pilih kategori, cari (“terima kasih”, “thanks” atau “谢谢” semuanya boleh) atau pilih daripada “Baru digunakan” — emoji dimasukkan di tempat kursor anda. Untuk jawapan tersimpan, taip “/” dan pilih daripada senarai “Balasan pantas”.',
      },
      s6: {
        title: 'Lampirkan fail (pilihan)',
        body: 'Klik ikon klip kertas, “Lampirkan fail”, dan pilih foto, PDF, video atau fail audio (jpg, png, webp, pdf, mp4, ogg atau mp3, sehingga 16 MB). Nama fail muncul di atas kotak; anda boleh taip kapsyen bersamanya.',
      },
      s7: {
        title: 'Hantar',
        body: 'Tekan Enter (Shift+Enter hanya menambah baris baharu) atau klik butang anak panah gelap di kanan. Mesej anda muncul di sebelah kanan perbualan, dengan tanda kecil “Dihantar”, “Diterima” dan “Dibaca” jika aplikasi melaporkannya.',
      },
      s8: {
        title: 'Tandakan sembang sebagai selesai',
        body: 'Apabila pelanggan sudah dibantu, klik “Selesaikan” di atas kanan perbualan. Butang bertukar menjadi “Buka semula”; jika pelanggan menulis lagi, sembang terbuka semula dengan sendirinya.',
      },
    },
    done: 'Mesej anda dipaparkan di kanan perbualan dengan tanda, dan selepas anda klik “Selesaikan” butang itu menjadi “Buka semula”.',
    tips: {
      t1: 'Gunakan tab “Nota peribadi” untuk meninggalkan pesanan kepada rakan sepasukan di dalam sembang — pelanggan tidak akan nampak.',
      t2: 'Tanda “Kotak pasir — tidak dihantar” bermaksud saluran ini belum disambungkan secara sebenar, jadi tiada apa-apa keluar dari ChatBranch. Minta pentadbir menyiapkan tetapan saluran.',
      t3: 'Jika pelanggan membalas STOP, sembang ditanda “Telah menarik diri” dan ChatBranch meminta anda mengesahkan sebelum menghantar — hantar hanya jika mereka bertanya sesuatu.',
      t4: 'Menulis jawapan panjang dengan senarai atau beberapa perenggan? Gunakan Shift+Enter antara baris, kemudian tekan Enter sekali untuk menghantar semuanya sebagai satu mesej.',
    },
    trouble:
      'Kotak mesej kelabu dan tertulis “Tetingkap 24 jam tertutup”: pada nombor WhatsApp Business anda mesti menghantar templat yang diluluskan dahulu — lihat “Hantar templat WhatsApp apabila tetingkap 24 jam tertutup”. Ikon klip kertas tiada: saluran ini tidak boleh menerima fail daripada ChatBranch (contohnya LINE, Viber atau SMS) — hantar teks sahaja. Tanda merah “Gagal”: mesej tidak sampai — minta pentadbir menyemak saluran itu di halaman Saluran.',
  },
  quoteAndReact: {
    title: 'Petik mesej atau beri reaksi emoji',
    summary: 'Di WhatsApp dan Telegram, balas satu mesej tertentu supaya pelanggan tahu mesej mana yang anda maksudkan, atau beri reaksi emoji tanpa menulis balasan.',
    before: 'Petikan dan reaksi berfungsi dalam perbualan WhatsApp dan Telegram. Anda perlu dibenarkan menghantar mesej dalam sembang itu.',
    steps: {
      s1: {
        title: 'Buka perbualan',
        body: 'Di bar sisi, di bawah Kerja, klik “Sembang” dan pilih perbualan WhatsApp atau Telegram.',
      },
      s2: {
        title: 'Tunjukkan tindakan mesej',
        body: 'Pada komputer, gerakkan tetikus ke atas mesej. Pada telefon atau tablet, tekan dan tahan mesej seketika. Satu bar kecil muncul di atasnya dengan enam emoji, “+” (“Lagi emoji”), “Balas” dan “Salin teks”.',
      },
      s3: {
        title: 'Beri reaksi emoji',
        body: 'Klik salah satu 👍 ❤️ 😂 😮 😢 🙏, atau “+” untuk memilih emoji lain. Reaksi anda muncul dalam cip kecil di bawah mesej dan dihantar kepada pelanggan. Klik emoji yang sama sekali lagi (atau cip anda) untuk menariknya balik; memilih emoji lain akan menggantikannya.',
      },
      s4: {
        title: 'Balas mesej itu',
        body: 'Klik “Balas”. Jalur “Membalas …” dengan mesej itu muncul di atas kotak mesej — klik × jika anda berubah fikiran.',
      },
      s5: {
        title: 'Tulis dan hantar',
        body: 'Taip jawapan anda dan tekan Enter. Mesej anda menunjukkan mesej yang dipetik di atasnya, dan aplikasi pelanggan memaparkannya sebagai balasan kepada mesej itu.',
      },
      s6: {
        title: 'Cari mesej asal',
        body: 'Apabila pelanggan memetik mesej terdahulu, petikan itu dipaparkan di bahagian atas gelembung mereka. Klik untuk melompat ke mesej asal, yang akan berkelip seketika.',
      },
    },
    done: 'Reaksi anda dipaparkan sebagai cip di bawah mesej, atau balasan anda menunjukkan mesej yang dipetik di atasnya.',
    tips: {
      t1: 'Halakan tetikus ke atas cip reaksi untuk melihat siapa yang memberi reaksi — pelanggan atau rakan sepasukan yang mana.',
      t2: 'Perniagaan hanya ada satu reaksi bagi setiap mesej: jika rakan sepasukan sudah memberi reaksi, reaksi anda menggantikannya.',
      t3: 'Reaksi pelanggan tidak sekali-kali membangunkan AI atau membuka semula sembang yang telah selesai — ia hanya dipaparkan pada mesej.',
    },
    trouble:
      '“WhatsApp 24h window closed — a reaction can only be sent within 24 hours…”: pada nombor WhatsApp Business, reaksi dikira sebagai mesej, jadi ia hanya berfungsi dalam tempoh 24 jam selepas mesej terakhir pelanggan. “REACTION_INVALID” di Telegram: Telegram hanya membenarkan set emoji reaksinya sendiri — cuba 👍, ❤️, 🔥 atau 🙏. Tiada “Balas” pada mesej: nota, baris sistem dan saluran lain (e-mel, Messenger, SMS…) tidak menyokong petikan.',
  },
  findChats: {
    title: 'Cari sembang yang betul dengan cepat',
    summary: 'Cari, gunakan tab sedia ada, tapis mengikut status, saluran atau orang, dan simpan penapis yang anda guna setiap hari sebagai tab anda sendiri.',
    steps: {
      s1: {
        title: 'Buka peti masuk',
        body: 'Di bar sisi, di bawah Kerja, klik “Sembang”.',
      },
      s2: {
        title: 'Cari mengikut nama, nombor atau perkataan',
        body: 'Klik kotak carian “Nama, nombor atau mesej” di atas senarai dan taip, contohnya, “0123” atau “bayaran balik”. Senarai mengecil kepada yang sepadan; klik × kecil untuk mengosongkannya.',
      },
      s3: {
        title: 'Gunakan tab',
        body: 'Di bawah kotak carian ada tab: “Semua”, “Milik saya” (sembang yang ditugaskan kepada anda), “Belum ditugaskan”, “Perlukan manusia”, kemudian paparan yang anda simpan. Klik satu tab untuk menukar senarai.',
      },
      s4: {
        title: 'Buka tetingkap penapis',
        body: 'Klik “Tapis” di sebelah kotak carian. Tetingkap “Tapis perbualan” terbuka dan mengira sembang yang sepadan di bahagian atas semasa anda membuat perubahan.',
      },
      s5: {
        title: 'Pilih status dan saluran',
        body: 'Di bawah “Status”, pilih contohnya “Terbuka” dan “Menunggu”. Di bawah “Saluran”, pilih aplikasi yang anda mahu, seperti WhatsApp atau E-mel.',
      },
      s6: {
        title: 'Pilih milik siapa',
        body: 'Di bawah “Penerima tugasan”, pilih “Saya” daripada “Pilihan pantas” (atau “Belum ditugaskan”, atau rakan sepasukan). Di bawah “Pasukan”, pilih pasukan jika anda menggunakan pasukan.',
      },
      s7: {
        title: 'Pilih keadaan sembang',
        body: 'Di bawah “Keadaan perbualan”, tandakan “Menunggu balasan kita” untuk melihat hanya sembang yang pelanggan menulis terakhir. Anda juga boleh menetapkan “Balasan automatik AI” kepada Hidup atau Mati, dan di bawah “Diserahkan kepada manusia” tetapkan “Penyerahan” kepada “Perlukan manusia”.',
      },
      s8: {
        title: 'Tunjukkan sembang',
        body: 'Klik butang gelap di bawah (contohnya “Tunjuk 12 sembang”). Setiap penapis aktif muncul sebagai cip kecil di atas senarai — klik × untuk membuangnya, atau “Kosongkan semua” untuk bermula semula.',
      },
      s9: {
        title: 'Simpan sebagai tab anda sendiri',
        body: 'Buka “Tapis” sekali lagi. Di sebelah “Simpan sebagai paparan”, taip nama seperti “WhatsApp menunggu saya” dan klik “Simpan”. Mesej memberitahu ia kini tab di atas senarai.',
      },
      s10: {
        title: 'Semak tab “Perlukan manusia”',
        body: 'Klik “Perlukan manusia” untuk melihat setiap sembang yang diserahkan oleh AI (atau peraturan) kepada manusia. Nombor ambar pada tab menunjukkan berapa yang menunggu.',
      },
    },
    done: 'Senarai hanya menunjukkan sembang yang anda mahu, dan paparan yang disimpan muncul sebagai tab di sebelah “Perlukan manusia”.',
    tips: {
      t1: 'Klik butang “…” di hujung tab (“Urus paparan tersimpan”) untuk menamakan semula, menyusun atau memadam paparan. Paparan anda peribadi — orang lain tidak nampak.',
      t2: 'Jika anda menukar penapis semasa paparan tersimpan dibuka, titik muncul pada tabnya dan butang “Kemas kini paparan” membolehkan anda menyimpan perubahan.',
      t3: 'Dalam tetingkap penapis, tetapkan “Isih” kepada “Paling lama menunggu” untuk menjawab pelanggan yang paling lama menunggu dahulu.',
      t4: 'Tekan Ctrl+K (⌘K pada Mac) di mana-mana untuk mencari kenalan, perbualan dan halaman sekali gus.',
    },
  },
  takeOverFromAi: {
    title: 'Ambil alih sembang daripada AI',
    summary: 'Apabila AI tidak dapat membantu, ia menyerahkan sembang kepada manusia. Begini cara mengenal pasti sembang itu, mengambilnya, menjawab sendiri dan menyerahkan semula kepada AI.',
    before: 'AI mesti sudah menjawab pelanggan. Anda perlukan kebenaran menghantar mesej dalam sembang (ejen dan pentadbir memilikinya).',
    steps: {
      s1: {
        title: 'Kenal pasti sembang yang memerlukan anda',
        body: 'Dalam “Sembang”, sembang yang diserahkan menunjukkan cip ambar dalam senarai, seperti “AI telah menyerahkan”. Nombor ambar pada tab “Perlukan manusia” mengiranya.',
      },
      s2: {
        title: 'Buka sembang',
        body: 'Klik tab “Perlukan manusia”, kemudian klik sembang itu.',
      },
      s3: {
        title: 'Baca sepanduk ambar',
        body: 'Di atas kotak mesej, sepanduk ambar tertulis “AI telah menyerahkan perbualan ini”, bersama sebabnya (contohnya “AI meminta manusia”) dan berapa lama dahulu. Semasa ia terbuka, AI biasanya berhenti menjawab sembang ini.',
      },
      s4: {
        title: 'Klik “Ambil alih”',
        body: 'Klik “Ambil alih” dalam sepanduk. Sembang ditugaskan kepada anda dan sepanduk menambah “kini milik anda; balasan anda menutupnya.”',
      },
      s5: {
        title: 'Jawab pelanggan sendiri',
        body: 'Taip jawapan dalam kotak mesej dan tekan Enter. Balasan anda menutup penyerahan — sepanduk dan cip ambar hilang. AI kekal mati untuk sembang ini sehingga seseorang menghidupkannya semula.',
      },
      s6: {
        title: 'Atau biar AI teruskan',
        body: 'Jika manusia tidak diperlukan, klik “Biar AI teruskan”. Ini menutup penyerahan dan menghidupkan semula chatbot untuk sembang ini. Jika mesej terakhir pelanggan masih menunggu jawapan, AI terus membalasnya.',
      },
      s7: {
        title: 'Matikan atau hidupkan AI untuk sembang ini sahaja',
        body: 'Di atas perbualan ada butang kecil bertanda “AI” dengan suis. Klik untuk mematikan (atau menghidupkan) AI bagi sembang ini sahaja; sembang lain tidak terjejas.',
      },
      s8: {
        title: 'Jika suis AI kelabu',
        body: 'Peraturan di atas sembang yang menentukan — contohnya AI dimatikan untuk seluruh perniagaan, di luar waktu AI, kenalan ditetapkan “Tidak pernah”, atau pelanggan telah menarik diri. Halakan tetikus ke suis untuk membaca sebabnya; pentadbir menukar peraturan itu di halaman Pemantau AI atau dalam “AI untuk kenalan ini”.',
      },
    },
    done: 'Sepanduk ambar hilang, sembang sudah keluar dari tab “Perlukan manusia”, dan suis AI menunjukkan keadaan yang anda pilih.',
    tips: {
      t1: 'Jika anda nampak “Balasan AI menunggu semakan”, AI telah menulis jawapan yang perlu diluluskan: klik “Luluskan & hantar”, “Tolak”, atau “Sunting dalam pemantau” untuk mengubahnya dahulu.',
      t2: 'Tinggalkan “Nota peribadi” menerangkan mengapa anda mengambil alih, supaya rakan sepasukan yang membuka sembang kemudian faham.',
      t3: 'Apabila selesai, hidupkan semula AI dengan suis di atas supaya ia boleh mengendalikan soalan pelanggan seterusnya.',
    },
    trouble:
      'Tiada butang “Ambil alih”: sembang sudah ditugaskan kepada anda (hanya “Biar AI teruskan” dipaparkan), atau peranan anda tidak boleh menghantar mesej — minta pentadbir. AI terus membalas selepas anda mematikannya: pastikan anda menukar suis sembang yang betul, dan semak “AI untuk kenalan ini” di halaman kenalan tidak ditetapkan kepada “Sentiasa”.',
  },
  assignChat: {
    title: 'Tugaskan sembang kepada rakan sepasukan atau pasukan',
    summary: 'Berikan perbualan seorang pemilik supaya semua orang tahu siapa yang menjawabnya — atau buang pemilik itu semula.',
    before: 'Rakan sepasukan anda perlukan log masuk sendiri (Pentadbiran → Pengguna & peranan). Pasukan dicipta oleh pentadbir di Pentadbiran → Pasukan. Pada komputer riba atau desktop, panel butiran berada di kanan sembang; pada telefon atau tablet ia dibuka sebagai laci daripada butang “Butiran perbualan” (ⓘ) di pengepala sembang.',
    steps: {
      s1: {
        title: 'Buka sembang',
        body: 'Di bar sisi, di bawah Kerja, klik “Sembang”, kemudian klik perbualan itu.',
      },
      s2: {
        title: 'Cari kad “Perbualan”',
        body: 'Lihat panel butiran di kanan dan cari kad “Perbualan”. Jika panel tersembunyi, klik butang bulat “i” (“Butiran perbualan” (ⓘ)) di atas kanan sembang. Pada telefon atau tablet, ketik butang “Butiran perbualan” (ⓘ) di pengepala sembang — panel yang sama muncul sebagai laci.',
      },
      s3: {
        title: 'Pilih rakan sepasukan',
        body: 'Klik kotak “Penerima tugasan” dan pilih nama (peranan mereka dalam kurungan). Ia disimpan serta-merta, dan sembang muncul dalam tab “Milik saya” orang itu.',
      },
      s4: {
        title: 'Atau pilih pasukan',
        body: 'Klik kotak “Pasukan” (hanya dipaparkan apabila perniagaan anda ada pasukan) dan pilih pasukan. Jika belum ada sesiapa ditugaskan, ChatBranch memberikan sembang kepada ahli pasukan seterusnya mengikut giliran.',
      },
      s5: {
        title: 'Buang pemilik',
        body: 'Untuk membatalkan tugasan, tetapkan “Penerima tugasan” kepada “Belum ditugaskan”. Untuk membuang pasukan, tetapkan “Pasukan” kepada “Tiada pasukan”.',
      },
      s6: {
        title: 'Semak sembang anda sendiri',
        body: 'Klik tab “Milik saya” di atas senarai untuk melihat setiap sembang yang ditugaskan kepada anda.',
      },
    },
    done: 'Kotak “Penerima tugasan” menunjukkan nama rakan sepasukan, dan sembang muncul dalam tab “Milik saya” mereka.',
    tips: {
      t1: 'Giliran (round-robin) dalam satu ayat: setiap kali pasukan menerima sembang tanpa pemilik, orang seterusnya dalam senarai pasukan mendapatnya, jadi kerja dibahagi sama rata.',
      t2: 'Untuk menugaskan banyak sembang sekali gus, gunakan mod Pilih — lihat “Bertindak ke atas banyak sembang sekali gus”.',
      t3: 'Gunakan tab “Belum ditugaskan” untuk mencari sembang yang belum dituntut sesiapa.',
    },
    trouble:
      'Anda tidak nampak kotak “Penerima tugasan”: buka panel butiran — klik butang bulat “i” pada skrin lebar, atau ketik “Butiran perbualan” (ⓘ) di pengepala sembang pada telefon atau tablet. Kotak “Pasukan” tiada: belum ada pasukan — minta pentadbir mencipta satu di Pentadbiran → Pasukan.',
  },
  quickReplies: {
    title: 'Guna dan cipta balasan pantas',
    summary: 'Balasan pantas ialah jawapan tersimpan yang anda masukkan dengan menaip “/” dalam kotak mesej. Jimatkan masa untuk soalan yang anda jawab setiap hari.',
    before: 'Sesiapa sahaja boleh menggunakan balasan pantas. Menciptanya memerlukan kebenaran “Urus templat mesej” (pentadbir memilikinya secara lalai).',
    steps: {
      s1: {
        title: 'Buka sembang dan klik kotak mesej',
        body: 'Dalam “Sembang”, buka perbualan dan klik kotak “Taip mesej (/ untuk balasan pantas)”.',
      },
      s2: {
        title: 'Taip “/” dan beberapa huruf',
        body: 'Taip garis condong diikuti permulaan kod pendek, contohnya “/hantar”. Senarai “Balasan pantas” muncul di atas kotak dengan sehingga enam padanan.',
      },
      s3: {
        title: 'Pilih satu dan hantar',
        body: 'Klik balasan yang anda mahu — atau gerak dengan kekunci ↑ dan ↓ dan tekan Enter atau Tab (Esc menutup senarai). Teksnya mengisi kotak, dengan butiran seperti nama pelanggan sudah diisi. Jika masih ada {{ruang isian}} (perbualan tanpa kenalan dipautkan), amaran dipaparkan di bawah kotak: gantikannya, kemudian tekan Enter.',
      },
      s4: {
        title: 'Atau gunakan butang pintasan',
        body: 'Apabila kotak kosong, sehingga lapan balasan pantas dipaparkan sebagai butang bulat kecil tepat di atasnya. Satu klik meletakkan balasan itu dalam kotak.',
      },
      s5: {
        title: 'Pergi ke Templat → Mesej',
        body: 'Untuk mencipta yang baharu, di bar sisi di bawah Pustaka klik “Templat”, kemudian tab “Mesej”.',
      },
      s6: {
        title: 'Klik “Balasan pantas baharu”',
        body: 'Klik “Balasan pantas baharu” di atas kanan senarai. Satu borang terbuka.',
      },
      s7: {
        title: 'Isi borang',
        body: '“Kod pendek”: apa yang ejen taip selepas “/”, contohnya “kemaskini-penghantaran” (huruf kecil, digit dan sengkang). Biarkan “Tersedia untuk” pada “Semua orang (global)” melainkan balasan itu hanya sesuai untuk sesetengah syarikat atau cawangan — kemudian pilih “Entiti terpilih” dan tandakannya (setiap satu dengan cawangannya). Di bawah “Tetapan lanjutan”, biarkan “Saluran” pada “Semua saluran” melainkan ia hanya sesuai untuk satu aplikasi. Tulis teks di bawah “Mesej” — butang pemboleh ubah memasukkan perkara seperti nama pelanggan.',
      },
      s8: {
        title: 'Simpan',
        body: 'Klik “Simpan balasan pantas”. Mesej mengesahkan ia disimpan dan memberitahu anda menaipnya selepas “/” dalam kotak mesej.',
      },
    },
    done: 'Menaip “/” dan kod pendek anda dalam mana-mana sembang menunjukkan balasan baharu anda dalam senarai “Balasan pantas”.',
    tips: {
      t1: 'Pastikan kod pendek ringkas dan mudah diteka, seperti “waktu”, “senarai-harga” atau “terima-kasih”.',
      t2: 'Hadkan balasan kepada saluran yang sesuai — contohnya jawapan panjang dengan tandatangan hanya untuk E-mel.',
      t3: 'Sunting atau padam balasan dengan ikon pensel atau tong sampah dalam senarai Mesej; perubahan sampai kepada semua orang serta-merta.',
    },
  },
  sendTemplate: {
    title: 'Hantar templat WhatsApp apabila tetingkap 24 jam tertutup',
    summary: 'WhatsApp hanya membenarkan perniagaan menghantar teks bebas dalam masa 24 jam selepas mesej terakhir pelanggan. Selepas itu, anda menghantar templat yang diluluskan untuk memulakan semula perbualan.',
    before: 'Anda perlukan nombor WhatsApp Business yang disambungkan melalui Meta, dan sekurang-kurangnya satu templat yang diluluskan. Pentadbir mencipta templat di Pustaka → Templat → “WhatsApp (diluluskan Meta)”; Meta menyemak setiap satu sebelum boleh digunakan.',
    steps: {
      s1: {
        title: 'Fahami peraturan 24 jam',
        body: 'Selama 24 jam selepas mesej terakhir pelanggan, anda boleh menulis apa sahaja. Selepas itu, WhatsApp hanya membenarkan mesej pra-lulus yang dipanggil templat. Sebaik sahaja pelanggan menjawab, anda mendapat 24 jam lagi.',
      },
      s2: {
        title: 'Buka sembang WhatsApp',
        body: 'Dalam “Sembang”, buka perbualan. Jika tetingkap sudah tertutup, kotak mesej kelabu dan tertulis “Tetingkap 24 jam tertutup — hantar templat untuk membukanya semula”.',
      },
      s3: {
        title: 'Klik butang templat',
        body: 'Klik ikon pertama di kiri kotak mesej, “Hantar templat mesej”. Apabila tetingkap tertutup ia dipaparkan gelap supaya mudah dicari.',
      },
      s4: {
        title: 'Pilih templat',
        body: 'Tetingkap “Hantar templat” menyenaraikan templat anda bersama bahasa dan jenisnya. Klik satu yang bertanda “Diluluskan”.',
      },
      s5: {
        title: 'Isi tempat kosong',
        body: 'Setiap tempat kosong dalam templat ada kotaknya sendiri. Kotak bertanda “Diisi automatik” datang daripada rekod kenalan; taip yang lain, contohnya tarikh seperti “12 Okt, 3 ptg”.',
      },
      s6: {
        title: 'Semak pratonton',
        body: 'Baca “Pratonton” di bawah — itulah tepat apa yang pelanggan akan terima.',
      },
      s7: {
        title: 'Hantar',
        body: 'Klik “Hantar templat”. Mesej muncul dalam perbualan.',
      },
      s8: {
        title: 'Tunggu pelanggan menjawab',
        body: 'Apabila pelanggan membalas, kotak mesej dibuka semula dan anda boleh menulis bebas lagi selama 24 jam.',
      },
    },
    done: 'Templat muncul dalam sembang, dan sebaik pelanggan menjawab kotak mesej tidak lagi kelabu.',
    tips: {
      t1: '24 jam dikira dari mesej terakhir pelanggan, bukan mesej anda — menghantar lebih banyak mesej tidak memanjangkannya.',
      t2: 'Nombor yang dipasangkan dengan kod QR (WhatsApp Web) tiada peraturan ini; templat yang dihantar di situ keluar sebagai teks biasa.',
      t3: 'Minta pentadbir menyediakan templat untuk situasi biasa anda (peringatan temu janji, kemas kini pesanan, “masih berminat?”) lebih awal — semakan Meta boleh mengambil masa.',
    },
    trouble:
      '“Tiada templat pada akaun ini”: pentadbir perlu mencipta satu di Templat → “WhatsApp (diluluskan Meta)”, dan templat langsung mesti diluluskan Meta dahulu. Butang templat tiada: ia hanya muncul dalam sembang WhatsApp. Penghantaran ditolak: templat dihantar dari nombor WhatsApp Business — minta pentadbir menyemak ada satu yang disambungkan dan sihat di halaman Saluran.',
  },
  addContact: {
    title: 'Tambah kenalan secara manual',
    summary: 'Simpan nama, telefon, e-mel, cawangan dan tag pelanggan supaya sembang mereka bersambung dan AI tahu dengan siapa ia bercakap.',
    before: 'Anda perlukan kebenaran “Cipta & sunting kenalan” (pentadbir memilikinya secara lalai; ejen hanya boleh melihat kenalan).',
    steps: {
      s1: {
        title: 'Buka Kenalan',
        body: 'Di bar sisi, di bawah Kerja, klik “Kenalan”.',
      },
      s2: {
        title: 'Klik “Tambah kenalan”',
        body: 'Klik butang gelap “Tambah kenalan” di atas kanan. Tetingkap “Kenalan baharu” terbuka.',
      },
      s3: {
        title: 'Taip nama',
        body: 'Dalam “Nama”, taip nama penuh orang itu, contohnya “Ahmad Fauzi”. Nama wajib diisi.',
      },
      s4: {
        title: 'Masukkan nombor telefon',
        body: 'Dalam “Telefon”, pilih dahulu kod negara di kiri (contohnya +60 untuk Malaysia), kemudian taip baki nombor tanpa 0 pertama — 012-345 6789 menjadi 12-345 6789. Telefon wajib diisi.',
      },
      s5: {
        title: 'Pilih bahasa',
        body: 'Di bawah “Bahasa”, pilih bahasa pilihan pelanggan. AI dan panggilan suara menggunakannya.',
      },
      s6: {
        title: 'Tambah e-mel dan syarikat (pilihan)',
        body: 'Isi “E-mel (pilihan)” dan “Syarikat (pilihan)” jika ada. E-mel membolehkan perbualan e-mel mereka bersambung secara automatik.',
      },
      s7: {
        title: 'Tambah tag (pilihan)',
        body: 'Dalam “Tag (dipisahkan koma)”, taip label dipisahkan dengan koma, contohnya “vip, borong”. Nama tag baharu dicipta untuk anda.',
      },
      s8: {
        title: 'Tandakan entiti',
        body: 'Jika perniagaan anda ada cawangan, tandakan di bawah “Entiti” yang mana kenalan ini milik (atau klik “Semua”). Isi mana-mana “Medan tersuai” yang perniagaan anda tambah — ia pilihan.',
      },
      s9: {
        title: 'Simpan',
        body: 'Klik “Tambah kenalan” di bawah. Tetingkap ditutup dan orang itu muncul dalam senarai kenalan.',
      },
    },
    done: 'Kenalan baharu muncul dalam senarai Kenalan; klik “Lihat” pada barisnya untuk membuka halaman kenalan.',
    tips: {
      t1: 'Cari dahulu dengan “Cari nama, telefon, e-mel atau tag…” untuk memastikan orang itu belum disimpan — ia mengelakkan pendua.',
      t2: 'Dengan kod negara disertakan, sembang WhatsApp dan SMS dari nombor ini bersambung dengan kenalan secara sendiri.',
      t3: 'Untuk menukar butiran kemudian, klik ikon pensel dalam lajur “Tindakan” senarai.',
    },
  },
  requestContactAccess: {
    title: 'Minta pelanggan yang milik cawangan lain',
    summary: 'Apabila telefon atau e-mel sudah menjadi pelanggan cawangan lain syarikat anda, minta pentadbir berkongsinya dengan cawangan anda dan bukannya menambah pendua.',
    before: 'Anda menambah kenalan dari cawangan anda (kebenaran “Cipta & sunting kenalan”). Kelulusan memerlukan pentadbir yang mengurus kedua-dua cawangan.',
    steps: {
      s1: {
        title: 'Tambah kenalan seperti biasa',
        body: 'Di “Kenalan”, klik “Tambah kenalan”, isi nama dan telefon atau e-mel, kemudian klik “Tambah kenalan”.',
      },
      s2: {
        title: 'Baca mesejnya',
        body: 'Jika nombor itu milik pelanggan cawangan lain yang anda tidak boleh lihat, tetingkap memaparkan “Pelanggan ini sudah wujud di cawangan lain …”. Nama dan butirannya kekal tersembunyi.',
      },
      s3: {
        title: 'Hantar permintaan',
        body: 'Taip nota pilihan untuk pentadbir (contohnya mengapa pelanggan kini bersama anda) dan klik “Minta akses”.',
      },
      s4: {
        title: 'Tunggu pentadbir',
        body: 'Pentadbir syarikat anda menerima pemberitahuan. Permintaan anda dipaparkan di bawah “Permintaan akses” di bahagian atas Kenalan, di mana anda juga boleh membatalkannya.',
      },
      s5: {
        title: 'Pentadbir: semak',
        body: 'Pentadbir klik pemberitahuan atau “Permintaan akses” di Kenalan. Setiap permintaan menunjukkan siapa yang meminta, untuk cawangan mana, nama pelanggan, 4 digit terakhir telefon, cawangan semasa mereka dan nota.',
      },
      s6: {
        title: 'Pentadbir: lulus atau tolak',
        body: 'Klik “Luluskan” untuk menambah pelanggan ke cawangan yang diminta (mereka kekal di cawangan lain), atau “Tolak” dengan sebab pilihan. Peminta akan diberitahu dalam kedua-dua keadaan.',
      },
    },
    done: 'Selepas diluluskan, pelanggan muncul dalam senarai Kenalan anda bersama semua sejarahnya.',
    tips: {
      t1: 'Meminta dua kali untuk pelanggan dan cawangan yang sama hanya menunjukkan permintaan yang sudah dihantar.',
      t2: 'Pelanggan yang menghantar mesej ke nombor cawangan anda dipautkan kepada kenalan yang betul dengan sendirinya — tiada permintaan diperlukan.',
    },
    trouble: 'Jika “Minta akses” mengatakan pautan telah tamat tempoh, klik “Tambah kenalan” sekali lagi: pautan permintaan sah selama 30 minit.',
  },
  importContacts: {
    title: 'Import kenalan daripada fail atau Google',
    summary: 'Bawa masuk buku kenalan yang anda sudah ada — sehingga 20,000 orang sekali gus — dalam empat langkah ringkas.',
    before:
      'Anda perlukan kebenaran “Cipta & sunting kenalan” (pentadbir). Sediakan fail: Excel (.xlsx), CSV, atau vCard (.vcf) yang dieksport dari iPhone atau telefon Android, sehingga 10 MB. Untuk import dari Google, sambungkan Google Contacts dahulu di Tetapan → Sambungan saya.',
    steps: {
      s1: {
        title: 'Buka wizard import',
        body: 'Dalam “Kenalan”, klik “Import” di atas kanan. Tetingkap “Import kenalan” terbuka dengan empat langkah di atas: “Sumber”, “Petakan lajur”, “Pendua” dan “Import”.',
      },
      s2: {
        title: 'Pilih dari mana kenalan datang',
        body: 'Klik “Muat naik fail” dan pilih fail anda — atau klik “Google Contacts” untuk membaca buku alamat Google anda sendiri. ChatBranch membaca fail dan beralih ke langkah seterusnya.',
      },
      s3: {
        title: 'Semak isi setiap lajur',
        body: 'Setiap lajur fail anda disenaraikan dengan beberapa contoh nilai dan senarai juntai bawah yang menyatakan isinya — ChatBranch meneka daripada tajuk. Betulkan tekaan yang salah, dan pilih “Jangan import” bagi lajur yang tidak diperlukan.',
      },
      s4: {
        title: 'Pastikan ada telefon atau e-mel',
        body: 'Sekurang-kurangnya satu lajur mesti telefon atau e-mel — kenalan perlukan cara untuk dihubungi. Kemudian klik “Seterusnya”.',
      },
      s5: {
        title: 'Tentukan apa berlaku kepada pendua',
        body: 'Anda nampak berapa yang “Baharu”, “Sudah ada”, “Digabung dalam fail” dan “Tidak boleh diimport”. Di bawah “Apabila kenalan sudah wujud”, pilih “Langkau”, “Isi medan kosong” atau “Tulis ganti”.',
      },
      s6: {
        title: 'Pilih cawangan (pilihan)',
        body: 'Di bawah “Tambah setiap kenalan ke”, pilih entiti (cawangan) mereka, atau biarkan “Tiada entiti”.',
      },
      s7: {
        title: 'Tag mereka dan cari saluran (pilihan)',
        body: 'Dalam “Tag setiap kenalan”, taip tag seperti “pameran-2026”. Tandakan “Cari saluran mereka selepas itu” untuk mendaftarkan WhatsApp, suara dan e-mel bagi setiap orang.',
      },
      s8: {
        title: 'Import',
        body: 'Klik “Import … kenalan” (butang menunjukkan bilangannya). Apabila siap anda nampak “Import selesai” dengan berapa yang dicipta, dikemas kini dan dilangkau.',
      },
      s9: {
        title: 'Muat turun baris yang tertinggal',
        body: 'Jika ada baris yang tidak dapat diimport, klik “Ralat (.csv)” (dan “Amaran (.csv)”) untuk memuat turunnya bersama nombor baris dan sebab. Betulkan dalam hamparan anda dan import fail itu semula. Klik “Tutup” apabila selesai.',
      },
    },
    done: '“Import selesai” menunjukkan berapa kenalan dicipta, dan orang baharu muncul dalam senarai Kenalan.',
    tips: {
      t1: 'Cuba fail kecil lima baris dahulu untuk memastikan lajur keluar dengan betul.',
      t2: 'Nombor telefon tanpa kod negara mendapat kod lalai organisasi anda (biasanya +60).',
      t3: 'Berikan setiap import tagnya sendiri — anda boleh mencari kumpulan itu kemudian dengan penapis tag di halaman Kenalan.',
    },
    trouble:
      'Ia meminta anda menyambung Google dahulu: buka Tetapan → Sambungan saya, sambungkan Google Contacts, kemudian kembali dan klik “Google Contacts” semula. “Seterusnya” kekal kelabu: tiada lajur ditetapkan sebagai telefon atau e-mel — petakan satu. Fail lebih besar daripada 10 MB: pecahkan kepada fail lebih kecil dan import satu demi satu.',
  },
  linkUnknownChat: {
    title: 'Pautkan sembang dari nombor tidak dikenali kepada kenalan',
    summary: 'Apabila seseorang yang tiada dalam rekod menulis, sambungkan sembang itu kepada kenalan sedia ada atau cipta yang baharu, supaya AI dan pasukan anda tahu siapa dia.',
    before: 'Memautkan kepada kenalan sedia ada: sesiapa yang boleh menghantar mesej. Mencipta kenalan baharu memerlukan kebenaran “Cipta & sunting kenalan” (pentadbir).',
    steps: {
      s1: {
        title: 'Kenal pasti sembang tidak dikenali',
        body: 'Buka sembang dalam “Sembang”. Pelanggan tidak dikenali menunjukkan nombor atau nama aplikasi dan bukannya nama orang, dan kad “Kenalan” di kanan menunjukkan kotak “Pautkan kepada kenalan…”. Pada telefon atau tablet, ketik “Butiran perbualan” (ⓘ) di pengepala sembang untuk membuka panel itu.',
      },
      s2: {
        title: 'Baca jalur di bawah tajuk',
        body: 'Jika peti masuk anda bertanya siapa pelanggan tidak dikenali, jalur di bawah tajuk menunjukkan hasilnya, contohnya “Pelanggan tidak dikenali — tiada kenalan yang sepadan”, bersama nama, telefon atau e-mel yang mereka berikan.',
      },
      s3: {
        title: 'Cari kenalan sedia ada',
        body: 'Dalam kad “Kenalan” di kanan, klik “Pautkan kepada kenalan…”, taip sebahagian nama atau nombor mereka dan pilih orang yang betul.',
      },
      s4: {
        title: 'Pautkan',
        body: 'Klik butang rantai kecil, “Pautkan kenalan”, di sebelah kotak. Sembang kini bergabung dengan perbualan lain orang itu.',
      },
      s5: {
        title: 'Atau cipta kenalan baharu',
        body: 'Jika mereka baharu, klik “Cipta kenalan” dalam jalur (atau “Cipta daripada sembang ini” di bawah kotak pautan). Tetingkap “Cipta kenalan daripada sembang ini” terbuka, sudah diisi dengan apa yang pelanggan katakan.',
      },
      s6: {
        title: 'Semak butiran dan simpan',
        body: 'Semak “Nama” (wajib), “Telefon” dan “E-mel”; biarkan “Entiti” sebagai “Entiti peti masuk” melainkan anda lebih tahu. Klik “Cipta & pautkan”.',
      },
      s7: {
        title: 'Sahkan ia berjaya',
        body: 'Kad “Kenalan” kini menunjukkan nama orang itu dengan butang “Nyahpaut”, dan jalur pelanggan tidak dikenali hilang.',
      },
    },
    done: 'Kad “Kenalan” menunjukkan nama orang itu dengan butang “Nyahpaut”.',
    tips: {
      t1: 'Sembang WhatsApp dan SMS bersambung sendiri apabila nombor telefon sepadan; sembang e-mel bersambung mengikut alamat. Sembang Messenger, Instagram, Telegram dan laman web biasanya perlu dipautkan secara manual.',
      t2: 'Tersalah pautkan orang? Klik “Nyahpaut” dan pautkan yang betul — tiada apa-apa dipadam.',
      t3: 'Di halaman kenalan, “Jemput ke aplikasi lain” menghantar pautan supaya sembang Messenger atau Telegram mereka yang seterusnya bersambung sendiri.',
    },
  },
  contactProfile: {
    title: 'Baca halaman kenalan',
    summary: 'Semua tentang seorang pelanggan di satu tempat: ringkasan AI, butiran, saluran, tag, cawangan, tempahan, cara AI melayan mereka dan setiap perbualan lalu.',
    steps: {
      s1: {
        title: 'Buka kenalan',
        body: 'Di bar sisi, di bawah Kerja, klik “Kenalan”. Cari orang itu, kemudian klik “Lihat” pada barisnya.',
      },
      s2: {
        title: 'Baca bahagian atas',
        body: 'Bahagian atas menunjukkan nama, telefon, e-mel, syarikat, status dan tag. “Buka di Sembang” terus membawa anda ke perbualan mereka.',
      },
      s3: {
        title: 'Dapatkan ringkasan AI',
        body: 'Di bawah “Ringkasan AI”, klik “Jana ringkasan” (atau “Jana semula” untuk yang baharu). Ia meringkaskan setiap panggilan, sembang dan e-mel menjadi taklimat pendek. “asas (tiada kunci AI)” bermaksud ringkasan lebih mudah kerana tiada perkhidmatan AI disediakan.',
      },
      s4: {
        title: 'Semak butiran dan medan tersuai',
        body: 'Di kanan, “Butiran” menunjukkan ID kenalan dan bahasa (anda boleh menukar bahasa di sini). “Medan tersuai” menyenaraikan medan tambahan perniagaan anda, dan “Rekod” menyenaraikan pesanan, tempahan atau tiket yang dipautkan.',
      },
      s5: {
        title: 'Lihat cara menghubungi mereka',
        body: 'Kad “Saluran” menyenaraikan setiap cara menghubungi orang itu — WhatsApp, e-mel, suara dan lain-lain — dengan bilangan mesej, “Ada di WhatsApp” atau “Belum disahkan”, dan sama ada mereka telah menarik diri. Gunakan “Cari saluran”, “Tambah saluran” atau “Jemput ke aplikasi lain” untuk menambah lagi.',
      },
      s6: {
        title: 'Tentukan cara AI melayan mereka',
        body: 'Dalam “AI untuk kenalan ini”, pilih “Ikut sembang” (biasa), “Tidak pernah” (AI tidak pernah menjawab orang ini — sesuai untuk VIP atau aduan) atau “Sentiasa”.',
      },
      s7: {
        title: 'Tetapkan tag mereka',
        body: 'Dalam kad “Tag”, tambah atau buang tag, kemudian klik “Simpan tag”. “Urus tag” membuka halaman Tag untuk mencipta yang baharu.',
      },
      s8: {
        title: 'Semak entiti mereka',
        body: 'Kad “Entiti” menunjukkan cawangan mana kenalan ini milik. Klik “Sunting” untuk menukarnya.',
      },
      s9: {
        title: 'Lihat tempahan mereka',
        body: 'Kad tempahan menyenaraikan temu janji akan datang. “Semua tempahan” membuka halaman Tempahan untuk kenalan ini.',
      },
      s10: {
        title: 'Baca sejarah penuh',
        body: 'Garis masa “Komunikasi” menunjukkan setiap panggilan, sembang dan e-mel, yang terbaharu dahulu. Klik “Muat log penuh” untuk melihat yang lebih lama.',
      },
    },
    done: 'Dari satu halaman ini anda boleh tahu siapa pelanggan itu, apa yang berlaku kali terakhir dan bagaimana AI patut melayan mereka.',
    tips: {
      t1: 'Jana ringkasan AI sebelum menelefon pelanggan — ia hanya beberapa saat dan menjimatkan masa membaca seluruh sejarah.',
      t2: 'Untuk menukar nama atau telefon, kembali ke senarai Kenalan dan klik ikon pensel pada baris mereka.',
      t3: 'Menetapkan “Tidak pernah” untuk pelanggan yang tidak berpuas hati memastikan manusia sentiasa menjawab mereka.',
    },
  },
  voiceCall: {
    title: 'Buat panggilan suara AI',
    summary: 'Biar AI menelefon pelanggan dari sembang mereka, ikuti panggilan secara langsung, bimbing atau tentukan ayatnya, dan letak telefon bila-bila anda mahu.',
    before:
      'Panggilan telefon memerlukan tetapan suara sekali sahaja (pelayan LiveKit dan talian telefon SIP), dibuat oleh sesiapa yang menjalankan pelayan ChatBranch anda — biasanya pemilik (superadmin). Sembang mesti dipautkan kepada kenalan yang nombor telefonnya ada kod negara.',
    steps: {
      s1: {
        title: 'Buka sembang pelanggan',
        body: 'Dalam “Sembang”, buka perbualan yang dipautkan kepada kenalan (dari halaman kenalan, “Buka di Sembang” membawa anda ke sana).',
      },
      s2: {
        title: 'Klik ikon telefon',
        body: 'Di atas kanan perbualan, klik ikon telefon, “Panggilan telefon AI (SIP)”. Ia menelefon nombor telefon sebenar kenalan. (Logo WhatsApp di sebelahnya memulakan panggilan WhatsApp pula, pada nombor yang dipasangkan dengan QR.)',
      },
      s3: {
        title: 'Lihat ia bersambung',
        body: 'Jalur di atas Sembang menunjukkan panggilan: “Mendail…”, kemudian “Berdering — sila jawab telefon”, kemudian langsung. Panel “Transkrip langsung — panggilan telefon AI” di atas mesej menunjukkan apa yang AI dan pemanggil katakan.',
      },
      s4: {
        title: 'Bimbing AI',
        body: 'Taip arahan dalam kotak panel, contohnya “Tawarkan slot 3 petang esok”, dan klik “Bimbing”. AI memasukkannya ke dalam apa yang ia katakan seterusnya.',
      },
      s5: {
        title: 'Sebut ayat tepat anda',
        body: 'Untuk ayat anda disebut tepat seperti ditaip, taip dan klik “Ambil alih”. Suara AI membacanya kata demi kata.',
      },
      s6: {
        title: 'Eskalasi jika perlu',
        body: 'Klik “Eskalasi” dan AI dengan sopan memberitahu pemanggil bahawa seseorang akan menghubungi mereka, kemudian menamatkan panggilan.',
      },
      s7: {
        title: 'Letak telefon',
        body: 'Klik butang merah “Tamatkan” dalam panel (atau ikon telefon merah di atas). Panggilan tamat dan direkodkan sebagai ditamatkan oleh operator.',
      },
      s8: {
        title: 'Semak panggilan kemudian',
        body: 'Panggilan muncul dalam sembang sebagai baris “Panggilan suara AI” yang boleh dibuka untuk membaca transkrip. Di bar sisi, Semakan → “Log panggilan” menyenaraikan setiap panggilan bersama transkrip dan ringkasan AI.',
      },
    },
    done: 'Panel langsung menunjukkan perbualan semasa ia berlaku, dan selepas panggilan ia muncul dalam sembang dan dalam Log panggilan.',
    tips: {
      t1: 'Tetapkan bahasa kenalan di halaman kenalannya — AI membuka dan meneruskan panggilan dalam bahasa itu.',
      t2: 'Pastikan arahan Bimbing pendek dan khusus; AI paling baik mengikut satu ayat yang jelas.',
      t3: 'Beberapa panggilan boleh berjalan serentak (biasanya sehingga tiga); setiap satu ada panelnya sendiri dalam sembangnya sendiri.',
    },
    trouble:
      'Jalur panggilan menunjukkan ❌ merah dengan mesej menyebut tetapan yang perlu diisi (LIVEKIT atau trunk): panggilan suara belum disediakan pada pelayan ini — minta pemilik (superadmin) atau sesiapa yang menjalankan pelayan. Tiada ikon telefon: sembang belum dipautkan kepada kenalan — pautkan dahulu (lihat “Pautkan sembang dari nombor tidak dikenali kepada kenalan”). Panggilan tidak berdering: semak nombor telefon kenalan ada kod negara yang betul.',
  },
  bookingsDaily: {
    title: 'Semak dan batalkan tempahan',
    summary: 'Lihat setiap temu janji yang ditempah oleh AI dan pasukan anda, cari satu dengan cepat, dan batalkan apabila pelanggan tidak dapat hadir.',
    before: 'Tempahan muncul sebaik sahaja pentadbir memberikan AI kalendar (Aplikasi bersambung di Pustaka → Kemahiran AI). Anda perlukan kebenaran “Lihat sembang” untuk melihatnya.',
    steps: {
      s1: {
        title: 'Buka Tempahan',
        body: 'Di bar sisi, di bawah Kerja, klik “Tempahan”.',
      },
      s2: {
        title: 'Pilih Kalendar, Akan datang atau Lepas',
        body: 'Tempahan dibuka pada “Kalendar”: setiap janji temu pada harinya — tukar “Bulan”, “Minggu” atau “Hari” di bahagian atas kanan, bergerak dengan anak panah, dan “Hari ini” kembali ke masa kini. Klik janji temu untuk membuka sembangnya atau membatalkannya. Untuk langkah di bawah, klik “Akan datang” (belum berlaku) atau “Lepas” (sudah berlaku) untuk melihat tempahan yang sama sebagai jadual.',
      },
      s3: {
        title: 'Baca jadual',
        body: '“Bila” menunjukkan tarikh, masa dan zon waktu; kemudian “Kenalan”, “Kalendar”, “Status” dan “Ditempah oleh”. “Ditempah” bermaksud ia ada dalam kalendar; “Dikhaskan” bermaksud ia dipegang untuk balasan AI yang belum dihantar.',
      },
      s4: {
        title: 'Cari tempahan',
        body: 'Taip nama atau tajuk dalam “Cari kenalan atau tajuk…”. Gunakan kotak status (“Ditempah atau dikhaskan” atau “Semua status”) dan “Semua kalendar” untuk mengecilkan senarai.',
      },
      s5: {
        title: 'Buka sembang atau acara',
        body: 'Dalam “Tindakan”, ikon sembang membuka perbualan dengan pelanggan itu; ikon kalendar membuka acara Google Calendar (untuk kalendar Google).',
      },
      s6: {
        title: 'Klik “Batal”',
        body: 'Pada baris yang ditempah dan akan datang, klik “Batal” dalam lajur “Tindakan”. Tetingkap bertanya “Batalkan tempahan ini?” dan menerangkan apa yang akan berlaku.',
      },
      s7: {
        title: 'Sahkan, kemudian maklumkan pelanggan',
        body: 'Klik “Batalkan tempahan” (atau “Kekalkan” untuk berhenti). Masa itu dikosongkan dan nota ditambah pada perbualan — tetapi pelanggan tidak dimaklumkan secara automatik, jadi hantar mesej kepada mereka dalam sembang.',
      },
    },
    done: 'Tempahan menunjukkan “Dibatalkan” dan nota mengenainya muncul dalam perbualan pelanggan.',
    tips: {
      t1: 'Membuka Tempahan dari kenalan? Hanya tempahan orang itu dipaparkan — klik × pada cip kenalan (“Tunjuk tempahan semua kenalan”) untuk melihat semua orang semula.',
      t2: '“Dikhaskan” bertukar menjadi “Ditempah” apabila balasan AI benar-benar dihantar, contohnya selepas seseorang meluluskannya di Pemantau AI.',
      t3: 'Sentiasa hantar mesej kepada pelanggan selepas membatalkan — ChatBranch tidak melakukannya untuk anda.',
    },
  },
  bulkActions: {
    title: 'Bertindak ke atas banyak sembang sekali gus',
    summary: 'Pilih beberapa perbualan dan hidupkan atau matikan AI, selesaikan atau tugaskan semuanya sekali gus.',
    before: 'Anda perlukan kebenaran “Urus sembang” (pentadbir secara lalai). Tanpanya, butang “Pilih” tidak muncul.',
    steps: {
      s1: {
        title: 'Kecilkan senarai dahulu',
        body: 'Dalam “Sembang”, gunakan tab, kotak carian atau “Tapis” supaya senarai menunjukkan sembang yang anda mahu ubah, contohnya semua sembang “Menunggu balasan kita”.',
      },
      s2: {
        title: 'Klik “Pilih”',
        body: 'Klik “Pilih” di sebelah tajuk Sembang. Setiap baris mendapat kotak tanda, dan butang bertukar menjadi “Selesai”.',
      },
      s3: {
        title: 'Tandakan sembang',
        body: 'Klik baris yang anda mahu. Satu bar muncul di bawah menunjukkan berapa yang dipilih.',
      },
      s4: {
        title: 'Atau pilih seluruh penapis',
        body: 'Klik “Pilih kesemua … dalam penapis ini” dalam bar bawah untuk memilih setiap sembang yang sepadan (sehingga 1000). “Hanya yang saya pilih” menukar semula.',
      },
      s5: {
        title: 'Hidupkan atau matikan AI, atau selesaikan',
        body: 'Klik “AI hidup”, “AI mati” atau “Selesaikan” dalam bar bawah. Ia dikenakan pada setiap sembang yang dipilih.',
      },
      s6: {
        title: 'Atau tugaskan mereka',
        body: 'Buka kotak “Tugaskan kepada…”, pilih rakan sepasukan (atau “Tiada sesiapa (batalkan tugasan)”) dan klik “Tugaskan”.',
      },
      s7: {
        title: 'Baca hasilnya dan selesaikan',
        body: 'Satu baris mengesahkan apa yang berlaku, contohnya “Diselesaikan · 12 perbualan”, dan menyatakan jika ada yang dilangkau. Klik “Selesai” di atas untuk keluar dari mod Pilih.',
      },
    },
    done: 'Baris pengesahan menunjukkan bilangan perbualan yang diubah, dan senarai dikemas kini.',
    tips: {
      t1: 'Tapis dahulu, kemudian “Pilih kesemua” — contohnya setiap sembang terbuka pada satu nombor WhatsApp — dan bukannya menandakan baris satu demi satu.',
      t2: 'Klik × dalam bar bawah (“Kosongkan pilihan”) untuk memulakan pilihan semula.',
      t3: 'Setiap perubahan pukal direkodkan sekali dalam log Aktiviti, supaya pentadbir boleh melihat siapa buat apa.',
    },
  },
}
