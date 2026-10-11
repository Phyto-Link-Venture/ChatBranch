import type { admin as source } from '../../en/manual/admin'
import type { Dict } from '../../../types'

export const admin: Dict<typeof source> = {
  inviteTeammate: {
    title: 'Tambah rakan sepasukan dan pilih peranannya',
    summary:
      'Cipta log masuk untuk rakan sekerja baharu, pilih apa yang dibenarkan untuknya, dan tentukan cawangan mana yang boleh dilihatnya. Mengambil masa kira-kira dua minit.',
    before:
      'Anda perlu menjadi pentadbir atau pentadbir super. Pentadbir boleh menambah ejen dalam organisasinya sendiri; hanya pentadbir super boleh menambah pentadbir lain. Tentukan nama pengguna dan kata laluan sementara (sekurang-kurangnya 10 aksara) untuk diberikan kepada orang baharu itu.',
    steps: {
      s1: {
        title: 'Buka Pengguna & peranan',
        body: 'Di bar sisi, buka Pentadbiran → “Pengguna & peranan”. Anda akan melihat jadual semua orang yang boleh log masuk, bersama peranan mereka, entiti yang boleh dilihat dan bila mereka kali terakhir log masuk.',
      },
      s2: {
        title: 'Kenali tiga peranan terbina dalam',
        body: 'Di bahagian atas halaman, tiga kad menerangkan peranan. “Pentadbir super” boleh melakukan semua perkara, di mana-mana. “Pentadbir” mengurus organisasinya sendiri: saluran, penghalaan, perjalanan, templat, tag dan log masuk ejen. “Ejen” mengendalikan sembang, panggilan dan kenalan dalam entiti yang ditugaskan kepadanya, tanpa tetapan.',
      },
      s3: {
        title: 'Klik “Tambah pengguna”',
        body: 'Klik butang “Tambah pengguna” di atas jadual. Tetingkap “Tambah pengguna” akan dibuka.',
      },
      s4: {
        title: 'Taip butiran log masuk',
        body: 'Isi “Nama pengguna” (contohnya aina — mesti unik), “Nama paparan” jika mahu (contohnya Aina Rahman) dan “Kata laluan” sekurang-kurangnya 10 aksara yang bukan kata laluan biasa. Taip nama pengguna sendiri — teks yang diisi secara automatik oleh pelayar tidak dikira.',
      },
      s5: {
        title: 'Pilih peranan',
        body: 'Buka senarai “Peranan” dan pilih Ejen, Pentadbir atau salah satu peranan tersuai anda (disenaraikan di bawah “Peranan tersuai”). Jika tidak pasti, pilih Ejen — anda boleh menukarnya kemudian.',
      },
      s6: {
        title: 'Pilih entiti yang boleh dilihat ejen',
        body: 'Apabila peranan ialah Ejen atau peranan tersuai, grid “Akses entiti” akan muncul. Tandakan cawangan atau syarikat tempat orang ini bekerja — menandakan entiti utama turut merangkumi semua di bawahnya. Biarkan semuanya tidak bertanda supaya dia boleh melihat seluruh organisasi.',
      },
      s7: {
        title: 'Simpan pengguna baharu',
        body: 'Klik “Tambah pengguna” di bahagian bawah tetingkap. Mesej “Pengguna … dicipta.” muncul dan orang itu dipaparkan dalam jadual. Berikan nama pengguna dan kata laluan secara bersemuka atau melalui mesej peribadi.',
      },
      s8: {
        title: 'Tetapkan semula kata laluan atau tukar peranan kemudian',
        body: 'Pada baris orang itu, tukar senarai “Peranan” untuk menukar peranannya serta-merta. Klik ikon kunci (“Tetapkan semula kata laluan”), taip kata laluan baharu dan klik “Tetapkan kata laluan” — ini melog keluarnya di semua tempat. Ikon terakhir menyahaktifkan atau mengaktifkan semula log masuk.',
      },
    },
    done: 'Rakan sepasukan baharu muncul dalam jadual dengan peranan yang betul, dan boleh log masuk dengan nama pengguna dan kata laluan yang anda berikan.',
    tips: {
      t1: 'Log masuk juga boleh dimiliki oleh pelanggan: pilih kenalan dalam lajur “Log masuk sebagai kenalan” dan orang itu log masuk sebagai pelanggan tersebut.',
      t2: 'Tukar apa yang boleh dilihat ejen kemudian dengan ikon bangunan (“Akses entiti”) pada barisnya.',
      t3: 'Minta orang baharu membuka lawatan aplikasi dari butang ? di bar atas sebelum mula mengubah apa-apa.',
    },
    trouble:
      'Jika simpanan gagal dengan “Nama pengguna diperlukan”, taip semula nama pengguna dengan tangan. Jika kata laluan ditolak, jadikannya sekurang-kurangnya 10 aksara dan kurang mudah diteka (bukan nama pengguna, bukan kata laluan yang terkenal). Jika anda tidak boleh memilih Pentadbir, hanya pentadbir super boleh mencipta pentadbir. Jika butang tiada, peranan anda tidak memegang “Cipta & urus pengguna” — minta pentadbir super.',
  },
  rolesPermissions: {
    title: 'Tetapkan apa yang boleh dilakukan oleh setiap peranan',
    summary:
      'Gunakan matriks kebenaran untuk melaraskan peranan terbina dalam atau mencipta peranan sendiri, seperti ketua pasukan, dengan tindakan yang anda tandakan sahaja.',
    before:
      'Hanya pentadbir super boleh membuka Kebenaran. Ketahui tindakan yang anda mahu berikan kepada peranan itu, contohnya “Lihat sembang” dan “Hantar mesej” tetapi bukan “Urus akaun saluran”.',
    steps: {
      s1: {
        title: 'Buka Kebenaran',
        body: 'Di bar sisi, buka Pentadbiran → “Kebenaran”. Anda akan melihat satu kad bagi setiap peranan, bersama bilangan pengguna yang memegangnya dan bilangan bahagian yang boleh dicapainya.',
      },
      s2: {
        title: 'Tambah peranan tersuai (pilihan)',
        body: 'Untuk membuat peranan baharu, klik “Tambah peranan”. Taip “Nama peranan”, contohnya Ketua Pasukan, dan klik “Cipta peranan”. Peranan baharu bermula tanpa sebarang akses.',
      },
      s3: {
        title: 'Buka peranan yang hendak diubah',
        body: 'Klik “Urus akses” pada kad peranan itu. Grid bahagian muncul — Kenalan, Sembang, Aliran, Saluran, AI, Tetapan dan lain-lain — masing-masing menunjukkan Tiada akses, Separa atau Akses penuh.',
      },
      s4: {
        title: 'Beri seluruh bahagian sekali gus',
        body: 'Tandakan kotak semak pada kad bahagian untuk memberi peranan itu setiap tindakan dalam bahagian tersebut. Nyahtanda untuk menarik semuanya. Gunakan “Cari sumber…” untuk mencari bahagian dengan cepat.',
      },
      s5: {
        title: 'Pilih tindakan satu per satu',
        body: 'Klik “Urus” pada kad bahagian untuk melihat setiap tindakan, seperti “Lihat sembang”, “Hantar mesej” atau “Urus sembang”. Hidupkan atau matikan setiap satu, kemudian klik “Guna”.',
      },
      s6: {
        title: 'Simpan perubahan',
        body: 'Tiada apa yang disimpan sehingga anda klik “Simpan perubahan”. Mesej “Kebenaran disimpan.” mengesahkannya, dan pemegang peranan itu mendapat akses baharu apabila halaman dimuatkan seterusnya.',
      },
      s7: {
        title: 'Berikan peranan kepada orang',
        body: 'Pergi ke Pentadbiran → “Pengguna & peranan” dan pilih peranan baharu dalam senarai “Peranan” pada baris seseorang, atau semasa menambah pengguna.',
      },
      s8: {
        title: 'Namakan semula atau padam peranan tersuai',
        body: 'Kembali ke senarai peranan, gunakan ikon pensel untuk menamakan semula peranan tersuai dan ikon tong sampah untuk memadamnya. Peranan yang masih diberikan kepada seseorang tidak boleh dipadam — pindahkan pengguna tersebut ke peranan lain dahulu.',
      },
    },
    done: '“Kebenaran disimpan.” muncul, dan kad peranan menunjukkan bilangan baharu bahagian yang boleh dicapainya.',
    tips: {
      t1: 'Empat tindakan khas untuk pentadbir super secara lalai: menyunting kebenaran, kunci aplikasi & penyedia, kunci & model AI, dan terma bil AI. Berikannya kepada peranan lain hanya jika anda benar-benar mempercayainya.',
      t2: 'Pentadbir super sentiasa memegang setiap kebenaran, jadi anda tidak boleh mengunci diri sendiri.',
      t3: 'Akses terletak di atas skop entiti: ejen dengan “Lihat sembang” tetap hanya melihat sembang entitinya sendiri.',
    },
    trouble:
      'Jika halaman mengatakan matriks diurus oleh pentadbir super, anda tidak log masuk sebagai pentadbir super. Jika perubahan seolah-olah tidak berkesan, pastikan anda telah klik “Simpan perubahan”, kemudian minta orang itu memuat semula halaman. Jika pemadaman peranan ditolak, peranan itu masih diberikan kepada seseorang.',
  },
  entitiesBranches: {
    title: 'Sediakan syarikat dan cawangannya',
    summary:
      'Entiti menggambarkan cara perniagaan anda disusun: syarikat di atas dan cawangan, outlet atau unit di bawahnya. Ia menentukan perbualan, kenalan dan tetapan mana yang dimiliki oleh siapa.',
    before:
      'Anda perlu menjadi pentadbir. Lakarkan struktur anda dahulu, contohnya: Northwind Trading (syarikat) → Cawangan KL, Cawangan Pulau Pinang.',
    steps: {
      s1: {
        title: 'Buka Entiti',
        body: 'Di bar sisi, buka Pentadbiran → “Entiti”. Senarai menunjukkan entiti anda sebagai pokok; syarikat di bahagian atas membawa lencana MAIN.',
      },
      s2: {
        title: 'Tambah syarikat',
        body: 'Klik “Tambah entiti”. Taip “Nama entiti”, contohnya Northwind Trading. Biarkan “Slug” kosong — ia diisi untuk anda.',
      },
      s3: {
        title: 'Jadikannya entiti utama',
        body: 'Dalam “Berada di bawah”, pilih “Tiada — jadikan entiti utama”. Klik butang simpan. Syarikat itu muncul dengan lencana MAIN.',
      },
      s4: {
        title: 'Tambah cawangan di bawahnya',
        body: 'Klik “Tambah entiti” sekali lagi, taip nama cawangan (contohnya Cawangan KL) dan dalam “Berada di bawah” pilih syarikat itu. Simpan. Cawangan itu muncul berinden di bawah syarikatnya.',
      },
      s5: {
        title: 'Ketahui apa yang diwarisi cawangan',
        body: 'Cawangan menggunakan secara automatik apa yang ditetapkan di peringkat atas: persona dan peraturan rumah AI, pengetahuan, kemahiran AI, templat, tag, pasukan, akaun saluran, pelajaran AI dan tetapan seperti waktu senyap. Cawangan boleh menambah miliknya sendiri atau mengatasi tetapan; peringkat paling hampir yang menang.',
      },
      s6: {
        title: 'Ketahui apa yang kekal bagi setiap cawangan',
        body: 'Kenalan, perbualan, draf AI dan angka papan pemuka dimiliki oleh satu cawangan sahaja. Syarikat melihat semua perbualan cawangannya; cawangan tidak pernah melihat milik syarikat atau cawangan lain. Kata laluan dan kunci penyedia sentiasa untuk seluruh organisasi.',
      },
      s7: {
        title: 'Sambungkan nombor kepada cawangan',
        body: 'Perbualan dimiliki oleh cawangan yang nombor atau peti masuknya menerima perbualan itu. Tetapkan ini bagi setiap peti masuk di Saluran, atau secara pukal di Pentadbiran → “Pautan” dengan “Akaun saluran ↔ entiti”.',
      },
      s8: {
        title: 'Pindah atau padam entiti',
        body: 'Klik ikon pensel pada baris untuk menyuntingnya; tukar “Berada di bawah” untuk memindahkannya ke syarikat lain, dan matikan “Aktif” untuk mengeluarkannya daripada penghalaan tanpa memadamnya. Ikon tong sampah memadamnya — entiti yang masih mempunyai cawangan di bawahnya tidak boleh dipadam.',
      },
    },
    done: 'Syarikat anda menunjukkan lencana MAIN dan cawangannya disenaraikan di bawahnya dalam pokok.',
    tips: {
      t1: 'Berikan ejen akses kepada cawangan mereka di Pentadbiran → “Pengguna & peranan” supaya mereka hanya melihat sembang cawangan itu.',
      t2: 'Gunakan penukar entiti di bahagian atas papan pemuka untuk melihat satu cawangan pada satu masa.',
      t3: 'Tambah tag entiti (contohnya Syarikat dalaman / Syarikat luar) di Pustaka → Tag untuk menerangkan jenis setiap unit.',
    },
    trouble:
      'Jika pemadaman ditolak, padam atau pindahkan cawangannya dahulu. Jika cawangan tidak boleh diletakkan di bawah syarikat lain, entiti mesti kekal dalam organisasi yang sama dan tidak boleh berada di bawah dirinya sendiri. Jika perbualan masuk ke cawangan yang salah, semak entiti milik peti masuk yang menerimanya.',
  },
  teams: {
    title: 'Cipta pasukan dan agihkan sembang baharu',
    summary:
      'Pasukan memiliki sekumpulan perbualan, seperti Kaunter hadapan atau Jualan. Sembang baharu boleh diberikan kepada ahlinya secara bergilir, supaya tiada mesej menunggu tanpa pemilik.',
    before:
      'Anda perlu menjadi pentadbir. Cipta pengguna dahulu di Pentadbiran → “Pengguna & peranan” — pasukan memerlukan ahli.',
    steps: {
      s1: {
        title: 'Buka Pasukan',
        body: 'Di bar sisi, buka Pentadbiran → “Pasukan”. Jadual menunjukkan setiap pasukan, ahlinya dan bilangan perbualan terbuka yang dimilikinya.',
      },
      s2: {
        title: 'Klik “Tambah pasukan”',
        body: 'Klik “Tambah pasukan”. Tetingkap dibuka dengan butiran pasukan dan senarai pengguna.',
      },
      s3: {
        title: 'Namakan pasukan',
        body: 'Taip “Nama pasukan”, contohnya Kaunter hadapan, dan jika mahu “Penerangan” seperti Soalan pelanggan datang dan tempahan.',
      },
      s4: {
        title: 'Pilih tempat pasukan dimiliki',
        body: 'Dalam “Milik”, pilih “Seluruh organisasi” atau satu entiti (syarikat atau cawangan). Pasukan cawangan ditawarkan untuk peti masuk cawangan tersebut.',
      },
      s5: {
        title: 'Hidupkan agihan bergilir',
        body: 'Hidupkan “Agihkan perbualan baharu secara bergilir”. Setiap perbualan baharu kemudian pergi kepada ahli seterusnya mengikut giliran, supaya kerja dibahagi sama rata.',
      },
      s6: {
        title: 'Tambah ahli',
        body: 'Di bawah “Ahli”, tandakan orang yang berada dalam pasukan ini. Pembilang di bawah menunjukkan bilangan yang anda pilih. Klik “Tambah pasukan” untuk menyimpan.',
      },
      s7: {
        title: 'Hantar sembang baharu peti masuk kepada pasukan',
        body: 'Di bar sisi, buka Saluran, klik “Tetapan” pada kad peti masuk dan pergi ke langkah “Siapa yang menjawab”. Pilih pasukan anda dalam “Pasukan” dan klik “Simpan & teruskan”. Perbualan baharu dalam peti masuk itu kini pergi kepada pasukan ini dahulu.',
      },
      s8: {
        title: 'Tukar atau padam pasukan kemudian',
        body: 'Gunakan ikon pensel pada baris pasukan untuk menukar ahlinya atau mematikan agihan bergilir. Ikon tong sampah memadamnya — perbualan terbukanya kembali tidak ditugaskan, tiada yang hilang.',
      },
    },
    done: 'Pasukan muncul dalam jadual bersama ahlinya dan lencana Bergilir, dan sembang baharu pada peti masuk yang dipilih menunjukkan pasukan itu sebagai pemilik.',
    tips: {
      t1: 'Perbualan dimiliki oleh pasukan dahulu dan oleh seorang ejen kemudian, jadi ia tetap ada pemilik walaupun belum diambil sesiapa.',
      t2: 'Automasi dan perjalanan juga boleh menyerahkan perbualan kepada pasukan.',
      t3: 'Tukar banyak keahlian sekali gus di Pentadbiran → “Pautan” dengan “Pasukan ↔ ahli”.',
    },
    trouble:
      'Jika sembang baharu tidak ditugaskan, pastikan agihan bergilir dihidupkan dan pasukan mempunyai ahli. Jika senarai ahli kosong, cipta pengguna dahulu. Jika peti masuk tidak menawarkan pasukan anda, pastikan pasukan itu milik entiti peti masuk tersebut atau untuk seluruh organisasi.',
  },
  tags: {
    title: 'Cipta tag dan tandakan kenalan anda',
    summary:
      'Tag menyatakan sesuatu itu apa — pelanggan VIP, pemandu, syarikat luar, sembang yang perlu disusuli. Perjalanan, automasi, templat dan AI semuanya boleh membacanya.',
    before:
      'Mencipta tag memerlukan kebenaran “Cipta & sunting tag” (pentadbir memilikinya). Fikirkan beberapa tag yang anda perlukan, contohnya VIP dan Pemandu.',
    steps: {
      s1: {
        title: 'Buka Tag',
        body: 'Di bar sisi, buka Pustaka → “Tag”. Gunakan “Tunjukkan” untuk bertukar antara tag “Kenalan”, “Entiti” dan “Perbualan”.',
      },
      s2: {
        title: 'Klik “Tag baharu”',
        body: 'Klik “Tag baharu”. Tetingkap dibuka dengan pratonton langsung cip tag di bahagian atas.',
      },
      s3: {
        title: 'Namakan dan pilih tempat ia digunakan',
        body: 'Taip nama, contohnya VIP. Di bawah “Digunakan pada”, pilih “Kenalan” (orang), “Entiti” (syarikat dan cawangan) atau “Perbualan” (label pada sembang dalam peti masuk).',
      },
      s4: {
        title: 'Pilih warna',
        body: 'Klik salah satu warna di bawah “Warna”. Cip pratonton berubah supaya anda nampak rupanya.',
      },
      s5: {
        title: 'Jadikan tag eksklusif dengan kumpulan (pilihan)',
        body: 'Taip “Kumpulan”, contohnya Keahlian, dan berikan kumpulan yang sama kepada Emas, Perak dan Gangsa. Sesuatu itu kemudian hanya boleh memegang satu tag daripada kumpulan itu — memilih yang baharu menggantikan yang lama.',
      },
      s6: {
        title: 'Tambah medan (pilihan)',
        body: 'Klik “Tambah medan” apabila setiap kenalan bertag memerlukan butirannya sendiri, contohnya No. lesen untuk Pemandu. Berikan “Label” dan jenis, dan tandakan Wajib jika ia mesti diisi.',
      },
      s7: {
        title: 'Cipta tag',
        body: 'Biarkan “Milik” pada “Seluruh organisasi” melainkan tag itu untuk satu entiti sahaja, kemudian klik “Cipta tag”. Ia muncul dalam senarai.',
      },
      s8: {
        title: 'Tandakan satu kenalan',
        body: 'Buka kenalan itu di Kerja → Kenalan. Dalam kad “Tag”, klik “Tambah tag…”, pilih tag, isi butirannya jika diminta dan klik “Simpan tag”.',
      },
      s9: {
        title: 'Tandakan banyak kenalan sekali gus',
        body: 'Kembali ke Pustaka → Tag, klik butang bilangan pada baris tag (contohnya “0 kenalan”). Hidupkan setiap kenalan yang patut memegangnya, kemudian klik “Simpan perubahan”.',
      },
    },
    done: 'Cip tag dipaparkan pada kenalan, dan baris tag dalam Pustaka → Tag menunjukkan bilangan kenalan yang memegangnya.',
    tips: {
      t1: 'Tag perbualan diletakkan pada sembang dari peti masuk, bukan dari halaman ini.',
      t2: 'Medan tag boleh digunakan dalam templat mesej, jadi peringatan boleh menyebut nombor lesen pemandu.',
      t3: 'Anda boleh menandakan tag sebagai tanpa-AI dalam kawalan AI, supaya kenalan dengannya sentiasa dijawab oleh manusia.',
    },
    trouble:
      'Jika anda tidak boleh menukar “Digunakan pada”, tag itu sudah digunakan — keluarkannya daripada semua dahulu. Jika kenalan kehilangan tag apabila anda menambah yang lain, kedua-dua tag berada dalam kumpulan yang sama. Jika “Tag baharu” tiada, minta pentadbir kebenaran “Cipta & sunting tag”.',
  },
  customFields: {
    title: 'Tambah medan pelanggan anda sendiri',
    summary:
      'Medan tersuai menambah butiran yang diperlukan perniagaan anda pada setiap pelanggan — seperti Gred, Hari pilihan atau Perlu pengangkutan — dan membolehkan anda menggunakannya dalam mesej.',
    before:
      'Anda memerlukan kebenaran “Urus medan tersuai” (pentadbir memilikinya). Tentukan nama dan jenis medan, contohnya Hari pilihan sebagai senarai juntai.',
    steps: {
      s1: {
        title: 'Buka Tetapan → Medan tersuai',
        body: 'Klik “Tetapan” di bahagian bawah bar sisi. Di bawah “Organisasi”, pilih “Medan tersuai”. Kad “Pelanggan” menyenaraikan medan yang sudah ada.',
      },
      s2: {
        title: 'Klik “Tambah medan”',
        body: 'Klik “Tambah medan” pada kad “Pelanggan”. Satu baris kosong baharu muncul.',
      },
      s3: {
        title: 'Taip label',
        body: 'Dalam “Label”, taip apa yang akan dilihat kakitangan pada borang pelanggan, contohnya Hari pilihan.',
      },
      s4: {
        title: 'Pilih jenis',
        body: 'Buka “Jenis” dan pilih Teks, Nombor, Ya / Tidak, “Senarai juntai (pilih satu)” atau “Pilihan berbilang (pilih banyak)”. Untuk senarai juntai, taip setiap pilihan di bawah “Pilihan” dan tekan Enter selepas setiap satu.',
      },
      s5: {
        title: 'Semak pratonton dan simpan',
        body: 'Tandakan “Wajib” jika medan mesti sentiasa diisi. Lihat pratonton di bahagian bawah kad untuk melihat rupa borang, kemudian klik “Simpan”. Mesej “Medan pelanggan disimpan.” muncul.',
      },
      s6: {
        title: 'Isikannya pada kenalan',
        body: 'Buka Kerja → Kenalan dan klik “Tambah kenalan”, atau ikon pensel pada kenalan. Medan baharu anda muncul dalam borang — isikan dan simpan. Ia kemudian dipaparkan dalam panel “Medan tersuai” di halaman kenalan.',
      },
      s7: {
        title: 'Gunakannya dalam templat mesej',
        body: 'Buka Pustaka → “Templat”, tab “Mesej”, dan klik “Balasan pantas baharu”. Di bawah kotak mesej, cip di bawah “Medan tersuai anda” memasukkan medan itu dengan satu klik.',
      },
      s8: {
        title: 'Ketahui rupa ruang letak',
        body: 'Ruang letak yang dimasukkan ialah cf_ diikuti kunci medan, di dalam kurungan kerinting berganda. Kunci ialah label dalam huruf kecil dengan garis bawah, jadi Hari pilihan menjadi cf_hari_pilihan. Apabila mesej dihantar, ia digantikan dengan nilai pelanggan.',
      },
    },
    done: '“Medan pelanggan disimpan.” muncul, dan medan baharu dipaparkan pada borang kenalan dan sebagai cip dalam penyunting templat.',
    tips: {
      t1: 'AI boleh membaca nilai medan tersuai, jadi Gred pelanggan boleh mempengaruhi balasannya.',
      t2: 'Butiran yang hanya untuk sesetengah kenalan, seperti nombor lesen pemandu, lebih sesuai sebagai medan tag.',
      t3: 'Perjalanan boleh menyemak medan tersuai dalam langkah keputusan, dan medan tarikh boleh mencetuskan perjalanan.',
    },
    trouble:
      'Jika butang “Simpan” kekal kelabu, belum ada apa-apa yang berubah. Jika ruang letak kosong dalam mesej yang dihantar, pelanggan itu tiada nilai untuk medan tersebut. Jika anda tidak boleh menyunting halaman, minta pentadbir kebenaran “Urus medan tersuai”.',
  },
  bindings: {
    title: 'Pautkan orang, pasukan, tag dan cawangan secara pukal',
    summary:
      'Halaman Pautan menetapkan banyak pautan sekali gus: entiti yang dilihat pengguna, siapa dalam pasukan mana, kenalan mana yang memegang tag, dan cawangan mana yang memiliki setiap nombor.',
    before:
      'Anda perlu menjadi pentadbir. Cipta pengguna, pasukan, tag dan entiti dahulu — Pautan hanya memautkan perkara yang sudah wujud.',
    steps: {
      s1: {
        title: 'Buka Pautan',
        body: 'Di bar sisi, buka Pentadbiran → “Pautan”. Halaman ini mempunyai tiga langkah bernombor.',
      },
      s2: {
        title: 'Pilih apa yang hendak dipautkan',
        body: 'Di bawah “1 · Apa yang hendak dipautkan”, klik satu kad: “Pengguna ↔ entiti”, “Pasukan ↔ ahli”, “Tag ↔ kenalan”, “Kenalan ↔ entiti”, “Tag ↔ entiti” atau “Akaun saluran ↔ entiti”. Setiap kad menerangkan fungsinya.',
      },
      s3: {
        title: 'Pilih satu perkara di sebelah kiri',
        body: 'Di bawah “2 · Pilih …”, buka senarai dan pilih pengguna, pasukan, tag atau akaun yang hendak diubah, contohnya pengguna aina.',
      },
      s4: {
        title: 'Hidupkan atau matikan seluruh kumpulan',
        body: 'Langkah 3 menunjukkan kad semua yang boleh dipautkan. Tandakan kotak semak kad untuk menghidupkan semua ahlinya, nyahtanda untuk mematikan semuanya. Pil menunjukkan Tiada, “k daripada N” atau “Semua N”.',
      },
      s5: {
        title: 'Pilih satu per satu',
        body: 'Klik “Urus” pada kad untuk menghidupkan atau mematikan ahli satu demi satu, kemudian klik “Guna”. Belum ada apa-apa yang disimpan.',
      },
      s6: {
        title: 'Simpan dengan bar Simpan',
        body: 'Bar “Perubahan belum disimpan” muncul di bawah. Klik “Simpan perubahan” untuk menyimpan semuanya sekali gus, atau “Buang perubahan” untuk membatalkan. Mesej menyatakan bilangan pautan yang ditambah dan dibuang.',
      },
      s7: {
        title: 'Ulang untuk item seterusnya',
        body: 'Pilih pengguna, pasukan atau tag lain dalam langkah 2 dan ulang. Jika anda bertukar sedangkan ada perubahan belum disimpan, anda akan ditanya sama ada hendak membuangnya.',
      },
    },
    done: 'Mesej “Disimpan — … dipautkan, … dinyahpautkan.” muncul, dan bilangan pada kad sepadan dengan pilihan anda.',
    tips: {
      t1: 'Untuk “Pengguna ↔ entiti”, membiarkan semuanya mati bermakna pengguna melihat seluruh organisasi.',
      t2: 'Akaun saluran dimiliki oleh satu entiti pada satu masa — memautkannya kepada entiti baharu akan memindahkannya.',
      t3: 'Menandakan entiti utama merangkumi semua cawangan di bawahnya.',
    },
    trouble:
      'Jika senarai mengatakan tiada apa untuk dipautkan, cipta item tersebut dahulu (pengguna, pasukan, tag atau entiti). Jika satu sisi ditanda baca sahaja, peranan anda tiada kebenaran untuk jenis itu. Jika menandakan kenalan membuang tag lain, kedua-dua tag berkongsi kumpulan.',
  },
  apiKeysMcp: {
    title: 'Cipta kunci API dan sambungkan aplikasi AI',
    summary:
      'Kunci API membolehkan skrip atau aplikasi AI seperti Claude atau Cursor menggunakan ChatBranch sebagai anda. Melalui MCP, aplikasi itu boleh mencari kenalan, membaca dan menjawab perbualan dan banyak lagi, dalam had kebenaran anda.',
    before:
      'Anda memerlukan kebenaran “Urus kunci API & MCP” (pentadbir memilikinya). Pasang aplikasi AI pada komputer anda. Rahsiakan kunci — sesiapa yang memilikinya boleh bertindak sebagai anda.',
    steps: {
      s1: {
        title: 'Buka Kunci API & MCP',
        body: 'Di bar sisi, buka Pentadbiran → “Kunci API & MCP”. Kad “Kunci API” menyenaraikan kunci anda.',
      },
      s2: {
        title: 'Klik “Cipta kunci”',
        body: 'Klik “Cipta kunci”. Tetingkap untuk mencipta kunci API dibuka. Dalam “Nama”, taip tempat anda akan menggunakannya, contohnya Claude Desktop pada komputer riba saya.',
      },
      s3: {
        title: 'Pilih akses',
        body: 'Di bawah “Akses”, pilih “Semua yang boleh saya lakukan”, atau “Hanya yang saya pilih” dan tandakan kebenaran yang diperlukan sahaja. Kunci tanpa sebarang kebenaran “urus” atau “hantar” hanya boleh membaca.',
      },
      s4: {
        title: 'Pilih tempoh dan cipta',
        body: 'Pilih “Tamat tempoh”, contohnya “90 hari”, kemudian klik butang cipta.',
      },
      s5: {
        title: 'Salin kunci sekarang',
        body: 'Tetingkap bertukar kepada “Salin kunci baharu anda”. Klik “Salin” dan simpan di tempat selamat, seperti pengurus kata laluan. Ini satu-satunya kali kunci dipaparkan.',
      },
      s6: {
        title: 'Pilih aplikasi AI anda',
        body: 'Klik “Selesai” dan tatal ke “Sambungkan aplikasi AI”. Pilih Claude Code, Claude Desktop, Cursor atau “Lain-lain”. “Titik akhir MCP” yang dipaparkan ialah alamat pelayan anda yang berakhir dengan /api/mcp.',
      },
      s7: {
        title: 'Salin tetapan siap sedia',
        body: 'Kunci baharu anda sudah diisi dalam contoh untuk lawatan ini. Salin arahan atau konfigurasi dan ikut petunjuk ringkas di bawahnya, contohnya menampalnya dalam tetapan MCP aplikasi dan memulakan semula aplikasi.',
      },
      s8: {
        title: 'Uji sambungan',
        body: 'Tampal kunci ke dalam “Tampal kunci API (cbk_…)” dan klik “Uji sambungan”. Baris “Disambungkan ke … alat tersedia dengan kunci ini” menunjukkan ia berfungsi.',
      },
      s9: {
        title: 'Batalkan kunci yang tidak digunakan lagi',
        body: 'Pada baris kunci, klik “Batalkan” dan sahkan. Apa sahaja yang menggunakannya berhenti berfungsi serta-merta.',
      },
    },
    done: 'Kunci dipaparkan sebagai Aktif dalam senarai, dan ujian menyatakan “Disambungkan ke …” dengan bilangan alat yang boleh anda gunakan.',
    tips: {
      t1: 'Buat satu kunci bagi setiap aplikasi atau komputer, supaya anda boleh membatalkan satu tanpa menjejaskan yang lain.',
      t2: 'Semua yang dilakukan oleh kunci direkodkan di Semakan → Aktiviti dengan sumber API atau MCP.',
      t3: 'Melalui MCP, tindakan yang memadam sesuatu meminta pengesahan dahulu.',
    },
    trouble:
      'Jika anda kehilangan kunci, batalkannya dan cipta yang baharu — ia tidak boleh dipaparkan lagi. Jika ujian gagal, pastikan anda menampal keseluruhan kunci yang bermula dengan cbk_ dan ia belum tamat tempoh atau dibatalkan. Jika aplikasi melihat kurang alat daripada dijangka, kunci atau peranan anda tiada kebenaran tersebut.',
  },
  aiBill: {
    title: 'Baca dan sediakan bil AI',
    summary:
      'Bil AI menunjukkan jumlah yang perlu dibayar setiap syarikat bagi penggunaan AI setiap bulan. Pentadbir super menetapkan mata wang, markup dan yuran bulanan sekali sahaja di Tetapan → Bil AI.',
    before:
      'Melihat bil memerlukan “Lihat bil penggunaan AI” (pentadbir memilikinya). Menetapkan terma dan melihat kos sebenar memerlukan “Tetapkan markup bil AI & lihat kos” (pentadbir super secara lalai). Syarikat ialah entiti utama; cawangannya dibilkan kepadanya.',
    steps: {
      s1: {
        title: 'Buka bil AI',
        body: 'Di bar sisi, buka Semakan → “Bil AI”. Pentadbir syarikat terus melihat penyata syarikatnya sendiri.',
      },
      s2: {
        title: 'Pilih bulan',
        body: 'Gunakan anak panah “Bulan sebelumnya” dan “Bulan seterusnya” di bahagian atas untuk bertukar bulan. Penyata menunjukkan tempoh yang diliputinya.',
      },
      s3: {
        title: 'Pilih syarikat',
        body: 'Jika anda melihat beberapa syarikat, pilih satu dalam senarai “Syarikat”, atau “Semua syarikat” untuk jadual ringkasan setiap syarikat.',
      },
      s4: {
        title: 'Baca penyata',
        body: 'Setiap baris ialah satu jenis kerja AI, seperti Balasan chat, Susulan atau Panggilan suara, dengan bilangan permintaan, token dan jumlahnya. Di bawahnya ada “Yuran platform bulanan” dan “Jumlah”. Carta menunjukkan penggunaan harian.',
      },
      s5: {
        title: 'Cetak atau simpan sebagai PDF',
        body: 'Klik “Cetak”. Dalam tetingkap cetak pelayar, pilih “Simpan sebagai PDF” untuk menyimpan fail, atau pilih pencetak. Menu disembunyikan supaya hanya penyata yang dicetak.',
      },
      s6: {
        title: 'Pentadbir super: buka terma bil',
        body: 'Klik “Terma bil” pada bil, atau buka Tetapan → Organisasi → “Bil AI”.',
      },
      s7: {
        title: 'Tetapkan mata wang, kadar tukaran dan markup',
        body: 'Taip “Mata wang (3 huruf)”, contohnya MYR, kadar tukaran dalam “1 USD = berapa MYR”, contohnya 4.70, “Markup (%)”, contohnya 30, dan yuran bulanan. Baris contoh menunjukkan nilai penggunaan USD 10 selepas dikira.',
      },
      s8: {
        title: 'Tetapkan terma untuk satu syarikat (pilihan)',
        body: 'Di bawah “Terma setiap syarikat”, taip markup atau yuran bulanan berbeza untuk sesebuah syarikat. Biarkan kotak kosong untuk menggunakan nilai lalai. Klik “Simpan terma bil”.',
      },
      s9: {
        title: 'Bandingkan kos dengan jumlah yang dibilkan',
        body: 'Kembali ke bil, pengurus bil melihat baris tambahan dengan kos anda, markup dan margin anda. Penggunaan yang bukan milik mana-mana syarikat — draf kemahiran, pengindeksan pengetahuan dan pembantu operator — disenaraikan berasingan dan tidak pernah dibilkan.',
      },
    },
    done: 'Penyata menunjukkan penggunaan bulan itu dalam mata wang anda, bersama yuran bulanan dan jumlah, dan dicetak dengan kemas.',
    tips: {
      t1: 'Syarikat hanya melihat jumlah yang dibilkan — tidak pernah kos atau margin anda.',
      t2: 'Kos diambil daripada jadual harga model, jadi kemas kini harga apabila penyedia AI anda mengubahnya.',
      t3: 'Cetak penyata setiap syarikat pada awal bulan untuk invoisnya.',
    },
    trouble:
      'Jika bulan itu menyatakan “Tiada penggunaan AI bulan ini.”, tiada kerja AI dikaitkan dengan syarikat itu. Jika senarai syarikat kosong, cipta entiti utama dahulu. Jika anda tidak nampak “Terma bil”, anda tiada kebenaran pengurus bil.',
  },
  activityLog: {
    title: 'Lihat siapa melakukan apa dalam log Aktiviti',
    summary:
      'Log Aktiviti merekodkan setiap perubahan yang dibuat oleh orang, kunci API, aplikasi AI, pembantu, automasi, perjalanan dan webhook — supaya anda sentiasa boleh menyemak siapa melakukan apa dan bila.',
    before: 'Anda memerlukan kebenaran “Lihat log aktiviti”. Ejen dan pentadbir memilikinya secara lalai.',
    steps: {
      s1: {
        title: 'Buka Aktiviti',
        body: 'Di bar sisi, buka Semakan → “Aktiviti”. Entri terbaharu berada di atas, dengan “Bila”, “Siapa”, “Tindakan” dan “Sasaran”.',
      },
      s2: {
        title: 'Pilih julat masa',
        body: 'Di bawah “Julat masa”, klik 24 jam, “7 hari”, 30 hari, 90 hari atau Sepanjang masa. Pilih “Tersuai” untuk memilih tarikh mula dan tamat.',
      },
      s3: {
        title: 'Cari',
        body: 'Taip dalam kotak carian (“Cari sasaran, butiran, laluan, IP, ID permintaan…”), contohnya nama kenalan atau nama pengguna.',
      },
      s4: {
        title: 'Tapis mengikut orang, tindakan atau sumber',
        body: 'Klik “Lebih banyak penapis” untuk memilih “Pelaku” (siapa yang melakukannya), “Tindakan” dan “Sumber” — contohnya Pengguna untuk orang, MCP untuk aplikasi AI atau Automasi untuk peraturan. Gunakan penapis entiti untuk melihat satu cawangan.',
      },
      s5: {
        title: 'Buka satu entri',
        body: 'Klik satu baris untuk melihat butirannya: cara ia dilakukan, teks butiran penuh dan maklumat permintaan. Gunakan “Lagi oleh …” untuk melihat semua yang dilakukan oleh orang itu.',
      },
      s6: {
        title: 'Lihat entri lebih lama',
        body: 'Di bahagian bawah, gunakan “Lebih lama” dan “Lebih baharu” untuk bertukar halaman, dan ubah bilangan baris bagi setiap halaman.',
      },
      s7: {
        title: 'Eksport ke hamparan',
        body: 'Klik “Eksport CSV”. Fail dengan entri yang sepadan dengan penapis anda dimuat turun, sedia dibuka dalam Excel atau Google Sheets.',
      },
    },
    done: 'Senarai hanya menunjukkan entri yang anda tapis, dan fail CSV dibuka dengan baris yang sama.',
    tips: {
      t1: 'Semak log Aktiviti dahulu apabila tetapan berubah dan tiada siapa ingat sebabnya.',
      t2: 'Kata laluan, kandungan mesej dan kunci rahsia tidak pernah ditulis ke dalam log.',
      t3: 'Gunakan “Semua dalam permintaan ini” pada entri untuk melihat semua perubahan daripada satu klik.',
    },
    trouble:
      'Jika halaman mengatakan anda memerlukan kebenaran, minta pentadbir “Lihat log aktiviti”. Jika tiada yang sepadan, luaskan julat masa atau kosongkan penapis. Jika eksport gagal, cuba julat masa yang lebih pendek.',
  },
  orgSettings: {
    title: 'Tetapkan tetapan seluruh organisasi',
    summary:
      'Beberapa tetapan terpakai kepada semua orang sekali gus: jenis pemberitahuan yang sampai kepada orang, dan kunci yang menyambungkan ChatBranch kepada Meta, Google, Shopify dan Stripe.',
    before:
      'Lalai pemberitahuan memerlukan “Urus lalai pemberitahuan organisasi” (pentadbir memilikinya). Kunci aplikasi & penyedia untuk pentadbir super sahaja. Sediakan nilai daripada Meta, Google, Shopify atau Stripe sebelum bermula.',
    steps: {
      s1: {
        title: 'Buka Tetapan',
        body: 'Klik “Tetapan” di bahagian bawah bar sisi. Bahagian “Organisasi” menyenaraikan halaman yang terpakai kepada semua orang; ikon mangga bermaksud peranan anda tidak boleh membuka halaman itu.',
      },
      s2: {
        title: 'Buka Lalai pemberitahuan',
        body: 'Klik “Lalai pemberitahuan”. Anda melihat satu suis bagi setiap jenis amaran: Mesej pelanggan, Kesihatan saluran, Aliran & automasi, Pentadbiran & konfigurasi dan Sistem.',
      },
      s3: {
        title: 'Senyapkan atau nyahsenyapkan jenis amaran',
        body: 'Matikan satu kategori untuk menjadikannya “Disenyapkan untuk semua” — tiada siapa menerima amaran itu, walau apa pun pilihan mereka. Hidupkan untuk “Hidup untuk semua”. Setiap perubahan disimpan serta-merta.',
      },
      s4: {
        title: 'Buka Kunci aplikasi & penyedia (pentadbir super)',
        body: 'Klik “Kunci aplikasi & penyedia”. Bahagian atas memegang “Token pengesahan webhook” yang dikongsi oleh semua nombor WhatsApp Business.',
      },
      s5: {
        title: 'Tetapkan token webhook Meta',
        body: 'Taip perkataan rahsia yang panjang dalam “Token pengesahan webhook” dan klik “Simpan kunci aplikasi”. Taip perkataan yang sama tepat dalam borang webhook di akaun pembangun Meta anda.',
      },
      s6: {
        title: 'Tambah kunci Google, Shopify dan Stripe',
        body: 'Di bawah “Kunci penyedia”, tampal ID klien dan rahsia Google daripada konsol Google Cloud anda, dan salin alamat ubah hala yang dipaparkan kembali ke Google. Tampal rahsia webhook Shopify dan Stripe jika anda menggunakannya. Klik “Simpan kunci penyedia”.',
      },
      s7: {
        title: 'Ketahui tempat tetapan lain',
        body: 'Medan tersuai dan Bil AI ialah halaman berasingan dalam bahagian yang sama. Kunci penyedia AI dan model berada di bawah bahagian “AI” dalam “Kunci & model AI”.',
      },
    },
    done: 'Setiap kategori menunjukkan “Hidup untuk semua” atau “Disenyapkan untuk semua” seperti pilihan anda, dan kunci yang disimpan menunjukkan mesej “Disimpan” lalu dipaparkan bertopeng.',
    tips: {
      t1: 'Setiap orang masih boleh mematikan kategori untuk diri sendiri di Tetapan → “Pemberitahuan saya”.',
      t2: 'Kunci yang disimpan dipaparkan bertopeng; biarkan kotak bertopeng jika mahu mengekalkan nilainya.',
      t3: 'Setelah klien Google disimpan, semua orang boleh menyambungkan akaun Google sendiri dari Tetapan → “Sambungan saya”.',
    },
    trouble:
      'Jika halaman menunjukkan ikon mangga, peranan anda tiada kebenarannya — minta pentadbir super. Jika Meta mengatakan webhook tidak dapat disahkan, token di kedua-dua belah mesti sama tepat. Jika log masuk Google gagal, pastikan alamat ubah hala di Google disalin dengan tepat.',
  },
  testLab: {
    title: 'Semak semuanya berfungsi dengan Makmal ujian',
    summary:
      'Makmal ujian menjalankan kira-kira empat puluh senario siap sedia — balasan AI, penyerahan, penghalaan, penarikan diri, tempahan dan lain-lain — melalui sistem sebenar, dan memberitahu yang mana lulus.',
    before:
      'Hanya pentadbir super boleh membuka Makmal ujian. Ia berfungsi di dalam entiti “Test lab” yang berpagar dan tidak pernah menyentuh pelanggan anda sendiri. Gunakannya pada pelayan ujian atau demo, jangan sekali-kali pada pelayan langsung — di sana ia patut dimatikan (TEST_LAB=0).',
    steps: {
      s1: {
        title: 'Buka Makmal ujian',
        body: 'Di bar sisi, buka Semakan → “Makmal ujian”. Anda melihat senario dikumpulkan mengikut kategori, dengan baris status yang menyatakan sama ada kunci AI sebenar atau otak ujian akan digunakan.',
      },
      s2: {
        title: 'Jalankan setiap senario',
        body: 'Klik “Jalankan semua”. Baris kemajuan menunjukkan senario yang sedang berjalan. Klik “Berhenti selepas yang ini” untuk berhenti awal.',
      },
      s3: {
        title: 'Atau jalankan sebahagian sahaja',
        body: 'Tandakan senario yang anda mahu, atau “Pilih semua dalam …” untuk satu kategori, kemudian klik “Jalankan yang dipilih”. Klik “Jalankan” pada satu senario untuk menjalankan yang itu sahaja.',
      },
      s4: {
        title: 'Baca keputusan',
        body: 'Setiap senario menunjukkan Lulus, Gagal atau Dilangkau dan bilangan semakan yang lulus. Klik “Butiran” untuk melihat langkah, apa yang dijangka, setiap semakan dan sebarang ralat.',
      },
      s5: {
        title: 'Buka apa yang dihasilkan senario',
        body: 'Dalam butiran, gunakan pautan di bawah “Buka apa yang dihasilkan” untuk melihat perbualan, kenalan atau draf ujian dalam papan pemuka biasa.',
      },
      s6: {
        title: 'Muatkan data demo',
        body: 'Klik “Muatkan data demo” untuk mengisi entiti Test lab dengan contoh kenalan, perbualan dan tempahan yang boleh anda terokai — berguna untuk melatih kakitangan baharu.',
      },
      s7: {
        title: 'Set semula data ujian',
        body: 'Klik “Set semula data ujian”, kemudian “Padam data ujian” untuk mengesahkan. Semua di dalam entiti Test lab dibuang; tiada apa di luarnya disentuh dan sejarah larian dikekalkan.',
      },
    },
    done: 'Baris akhir menyatakan bilangan senario yang lulus, gagal dan dilangkau, dan larian itu muncul di bawah “Sejarah larian”.',
    tips: {
      t1: 'Jalankan semua senario selepas menukar tetapan atau menaik taraf, untuk mengesan masalah lebih awal.',
      t2: 'Tanpa kunci AI, makmal menggunakan otak ujian yang boleh dijangka, jadi keputusan tidak bergantung pada model.',
      t3: 'Pembangun boleh menjalankan senario yang sama dari terminal dengan npm run test:scenarios.',
    },
    trouble:
      'Jika halaman mengatakan Makmal ujian dimatikan, pelayan berjalan dengan TEST_LAB=0 — itu betul untuk pelayan langsung. Jika senario gagal, buka “Butiran” untuk melihat semakan mana yang gagal dan sebabnya. Jika “Muatkan data demo” mengatakan ia sudah dimuatkan, set semula data ujian dahulu.',
  },
  requireTwoStep: {
    title: 'Wajibkan log masuk dua langkah',
    summary: 'Pastikan semua orang — atau pentadbir sahaja — log masuk dengan kod daripada aplikasi pengesah. Sesiapa yang belum menyediakannya dibawa ke skrin persediaan sebelum boleh berbuat apa-apa.',
    before: 'Anda perlu menjadi pentadbir super (atau memegang “Urus keselamatan log masuk”). Sediakan log masuk dua langkah untuk diri anda dahulu (Tetapan → Akaun saya) — peraturan ini juga meliputi anda.',
    steps: {
      s1: {
        title: 'Buka Tetapan → Keselamatan',
        body: 'Klik “Tetapan” di bahagian bawah bar sisi, kemudian “Keselamatan” di bawah “Organisasi”.',
      },
      s2: {
        title: 'Pilih siapa yang wajib menggunakannya',
        body: 'Pilih “Pentadbir dan pentadbir super” atau “Semua orang”. Kad itu menunjukkan berapa ramai yang sudah menggunakannya.',
      },
      s3: {
        title: 'Klik “Simpan”',
        body: 'Mulai sekarang, sesiapa yang diliputi peraturan dan belum menyediakannya dibawa ke “Sediakan log masuk dua langkah” kali seterusnya mereka menggunakan ChatBranch.',
      },
      s4: {
        title: 'Bantu sesiapa yang kehilangan telefon',
        body: 'Buka Pentadbir → “Pengguna & peranan” dan klik butang perisai pada baris mereka (“Tetapkan semula log masuk dua langkah”). Mereka log masuk dengan kata laluan sahaja dan menyediakannya semula.',
      },
    },
    done: 'Tetapan → Keselamatan menunjukkan pilihan anda dan berapa ramai yang menggunakan log masuk dua langkah.',
    tips: {
      t1: 'Kunci API dan pelayan MCP tidak terjejas — ia tidak pernah log masuk dengan kata laluan.',
      t2: 'Untuk berpatah balik, pilih “Tiada siapa diwajibkan”; mereka yang menghidupkannya sendiri mengekalkannya.',
    },
    trouble: 'Tiada “Keselamatan” dalam Tetapan: hanya pentadbir super (atau peranan yang diberi “Urus keselamatan log masuk”) dapat melihatnya. Diminta menyediakannya sejurus selepas menyimpan: peraturan ini juga terpakai kepada anda — imbas kod dan teruskan.',
  },
}
