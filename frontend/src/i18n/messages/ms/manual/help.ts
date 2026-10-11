import type { faq as faqSource, glossary as glossarySource } from '../../en/manual/help'
import type { Dict } from '../../../types'

export const faq: Dict<typeof faqSource> = {
  aiNotReplying: {
    q: 'AI tidak membalas pelanggan',
    a: 'Pertama, buka “Pemantau AI” dan pastikan “Suis utama AI” HIDUP (dan jika ia mengikut jadual, pastikan waktu sekarang berada dalam jadual). Kemudian buka sembang itu dan pastikan suis AI menunjukkan “AI hidup”. Seterusnya, semak kenalan: “AI untuk kenalan ini” tidak boleh ditetapkan kepada jangan sekali-kali, kenalan tidak boleh ada tag tanpa-AI, dan sembang mesti dipautkan kepada kenalan. Kemudian pastikan pelanggan tidak menghantar STOP, dan lihat di bawah “Menunggu semakan” jika balasan sedang ditahan untuk diluluskan oleh manusia. Akhir sekali, jika tiada kunci AI di Tetapan → “Kunci & model AI”, AI hanya memberi jawapan latihan (kotak pasir) — minta pentadbir menambahnya.',
  },
  aiWrongAnswer: {
    q: 'AI memberi jawapan yang salah atau pelik',
    a: 'Pertama, tambah fakta yang betul ke dalam “Pangkalan pengetahuan” supaya AI boleh menemuinya. Kemudian, jika ia peraturan yang mesti sentiasa dipatuhi, tambah peraturan rumah di “Persona & peraturan”. Seterusnya, tekan 👎 pada balasan yang salah dalam sembang dan taip jawapan yang betul sebagai pembetulan, supaya AI belajar daripadanya. Akhir sekali, tanya soalan yang sama di “Ruang uji AI” untuk memastikan jawapannya kini betul.',
  },
  messageFailed: {
    q: 'Mesej saya ada tanda merah dan tidak dihantar',
    a: 'Pertama, letakkan tetikus pada tanda merah untuk membaca sebabnya. Jika ia mengatakan tetingkap 24 jam sudah tutup, pelanggan kali terakhir menulis lebih sehari lalu di WhatsApp — hantar templat yang diluluskan, bukan teks bebas. Kemudian pastikan pelanggan tidak menarik diri (menghantar STOP). Seterusnya, lihat sama ada nombor yang menghantar mesej terputus di halaman “Saluran”. Akhir sekali, jika ini mesej pertama anda kepada orang yang tidak pernah menulis kepada anda, pengawal mesej sejuk mungkin sengaja menghalangnya.',
  },
  window24h: {
    q: 'Apakah tetingkap 24 jam dan kenapa saya tidak boleh menaip sesuka hati?',
    a: 'Pertama, ketahui bahawa WhatsApp (dan beberapa aplikasi lain) hanya membenarkan perniagaan menghantar mesej biasa selama 24 jam selepas mesej terakhir pelanggan. Selepas itu, anda hanya boleh menghantar templat yang diluluskan dari “Templat”. Kemudian, sebaik sahaja pelanggan membalas, tetingkap dibuka semula dan anda boleh menaip sesuka hati. Senarai sembang boleh menapis perbualan mengikut sama ada tetingkap masih terbuka.',
  },
  customerStoppedMessages: {
    q: 'Pelanggan tidak lagi menerima mesej',
    a: 'Pertama, semak sembang: jika pelanggan menghantar STOP atau UNSUBSCRIBE (atau BERHENTI), mereka telah menarik diri dan tiada lagi mesej dihantar kepada mereka, mengikut undang-undang dan reka bentuk. Kemudian, jika mereka mahu menerima mesej semula, mereka hanya perlu menghantar START (atau MULA) dari telefon mereka dan mereka akan disertakan semula. Anda masih boleh menghantar satu mesej secara manual dengan paksa, tetapi lakukannya hanya apabila pelanggan jelas memintanya.',
  },
  numberDisconnected: {
    q: 'Nombor WhatsApp menunjukkan Terputus atau Disekat',
    a: 'Pertama, buka “Saluran” dan lihat lencana kesihatan pada kad nombor itu; tekan “Semak sekarang” untuk mengujinya semula. Kemudian, bagi nombor peribadi yang dipasangkan dengan QR, buka kadnya dan imbas kod QR baharu dengan WhatsApp di telefon itu untuk memasangkannya semula. Seterusnya, bagi nombor WhatsApp Business (Cloud), minta pentadbir memastikan token akses Meta belum tamat tempoh dan tampal token baharu. Semasa nombor terputus, perbualan dialihkan ke nombor lain yang sihat jika boleh.',
  },
  emailNotArriving: {
    q: 'E-mel pelanggan tidak muncul dalam Sembang',
    a: 'Pertama, pastikan peti mel di halaman “Saluran” mempunyai butiran mel masuk (IMAP), bukan hanya penghantaran (SMTP). Kemudian, bagi Gmail atau Outlook, gunakan kata laluan aplikasi dan bukan kata laluan biasa anda. Seterusnya, lihat di bawah “Bukan kenalan” di bahagian atas senarai sembang — mel daripada orang yang belum menjadi kenalan masuk ke situ (balasan automatik seperti “di luar pejabat” tidak dimasukkan). Akhir sekali, mel diambil setiap beberapa ketika — tunggu seminit, kemudian buka semula Sembang.',
  },
  widgetNotShowing: {
    q: 'Gelembung sembang tidak muncul di laman web saya',
    a: 'Pertama, buka widget anda di halaman “Saluran” dan salin kod benam sekali lagi. Kemudian pastikan keseluruhan kod ditampal ke dalam laman web anda, sejurus sebelum tag body penutup, dan halaman telah diterbitkan. Seterusnya, kosongkan cache pelayar atau buka laman dalam tetingkap peribadi. Akhir sekali, pastikan widget dihidupkan dan laman web anda boleh mencapai alamat ChatBranch.',
  },
  callFails: {
    q: 'Panggilan suara tidak dapat dimulakan',
    a: 'Pertama, baca mesej pada skrin: jika ia mengatakan panggilan suara belum dikonfigurasi, minta pentadbir menyediakan perkhidmatan panggilan — ChatBranch tidak pernah memalsukan panggilan. Kemudian pastikan nombor telefon bermula dengan kod negara, contohnya +60123456789. Seterusnya, pastikan kenalan tidak ditanda jangan-hubungi dan tidak menarik diri. Akhir sekali, lihat “Log panggilan” untuk melihat bagaimana panggilan sebelum ini berakhir.',
  },
  importSkipped: {
    q: 'Sesetengah kenalan dilangkau semasa import',
    a: 'Pertama, pada langkah terakhir import, muat turun “Ralat (.csv)” untuk melihat setiap baris yang dilangkau dan sebabnya. Kemudian betulkan nombor telefon supaya ada kod negara (contohnya +60…), atau tetapkan negara lalai dalam tetapan anda. Seterusnya, ingat pilihan pendua yang anda buat: “langkau” tidak menyentuh kenalan sedia ada, jadi baris itu dikira sebagai dilangkau. Akhir sekali, import semula fail yang telah dibetulkan.',
  },
  cantSeePage: {
    q: 'Saya tidak nampak sesuatu halaman, atau ia kata saya tiada akses',
    a: 'Pertama, ketahui bahawa apa yang anda lihat bergantung pada peranan dan kebenarannya — ini perkara biasa, bukan kerosakan. Kemudian minta pentadbir memberikan kebenaran yang anda perlukan kepada peranan anda di “Pengguna & peranan”. Akhir sekali, log keluar dan log masuk semula selepas perubahan supaya menu dikemas kini.',
  },
  wrongEntityData: {
    q: 'Saya nampak kenalan atau sembang cawangan yang salah',
    a: 'Pertama, lihat penukar entiti di bahagian atas skrin — ia menentukan perniagaan atau cawangan yang sedang anda lihat. Kemudian pilih yang betul, atau “Semua entiti” untuk melihat semua yang anda dibenarkan lihat. Jika cawangan yang anda perlukan tiada, minta pentadbir menetapkan anda kepadanya.',
  },
  billLooksLow: {
    q: 'Bil AI kelihatan terlalu rendah (atau sifar)',
    a: 'Pertama, ketahui bahawa mesej AI selalunya berharga kurang daripada satu sen setiap satu, jadi jumlah kecil adalah biasa. Kemudian semak penggunaan yang tidak diagihkan — kerja seperti merangka kemahiran atau pembantu tidak dibilkan kepada mana-mana syarikat. Akhir sekali, semak markah naik: bil ialah kos AI sebenar ditambah markah naik dan yuran bulanan yang ditetapkan di Tetapan → “Bil AI” — tanpa markah naik, syarikat melihat harga kos.',
  },
  guideNotShowing: {
    q: 'Panduan halaman tidak muncul',
    a: 'Pertama, tekan butang “?” di bahagian atas skrin dan pilih “Tunjukkan panduan halaman ini”. Kemudian, jika anda mahu panduan bermula sendiri, tandakan “Tunjukkan panduan pada lawatan pertama” dalam menu yang sama. Panduan hanya bermula sendiri kali pertama anda membuka sesuatu halaman.',
  },
  forgotPassword: {
    q: 'Saya terlupa kata laluan',
    a: 'Pertama, minta pentadbir dalam syarikat anda menetapkan semula kata laluan anda di “Pengguna & peranan”. Kemudian log masuk dengan kata laluan baharu dan tukarkannya kepada kata laluan yang hanya anda tahu.',
  },
  slowFirstLoad: {
    q: 'Halaman lambat kali pertama saya membukanya',
    a: 'Pertama, beri beberapa saat — sesetengah halaman mengambil masa lebih lama kali pertama dimuatkan. Kemudian, jika ia tetap lambat setiap kali, muat semula halaman dan semak sambungan internet anda. Jika masih lambat, beritahu pentadbir anda halaman yang mana.',
  },
}

export const glossary: Dict<typeof glossarySource> = {
  entity: {
    term: 'Entiti',
    def: 'Sebuah perniagaan, jenama atau lokasi dalam ChatBranch. Setiap satu mempunyai kenalan, sembang dan tetapannya sendiri.',
  },
  branch: {
    term: 'Cawangan (outlet)',
    def: 'Entiti yang berada di bawah entiti lain, seperti kedai di bawah ibu pejabatnya. Ia menggunakan tetapan ibu pejabat melainkan ia menetapkan tetapannya sendiri.',
  },
  inbox: {
    term: 'Peti masuk (akaun saluran)',
    def: 'Satu tempat bersambung yang boleh digunakan pelanggan untuk menghubungi anda: nombor WhatsApp, peti mel e-mel, bot Telegram, halaman Facebook atau sembang laman web. Sediakannya di halaman “Saluran”.',
  },
  conversation: {
    term: 'Perbualan (sembang)',
    def: 'Semua mesej antara anda dan seorang pelanggan pada satu saluran. Anda membaca dan menjawabnya di “Sembang”.',
  },
  contact: {
    term: 'Kenalan',
    def: 'Pelanggan yang disimpan dengan nama, telefon, e-mel dan nota. Seorang kenalan boleh mempunyai sembang di banyak saluran.',
  },
  team: {
    term: 'Pasukan',
    def: 'Sekumpulan ejen yang berkongsi kerja. Sembang baharu boleh diserahkan kepada pasukan dan diagihkan antara ahlinya.',
  },
  tag: {
    term: 'Tag',
    def: 'Label berwarna yang anda lekatkan pada kenalan, perbualan atau entiti, seperti VIP atau Prospek baharu. Tag membantu anda menyusun, menapis dan mengautomasi.',
  },
  handover: {
    term: 'Serahan',
    def: 'Apabila sembang diserahkan daripada AI kepada manusia. Ia muncul di tab “Perlukan manusia” sehingga seseorang mengambilnya.',
  },
  aiMonitor: {
    term: 'Pemantau AI',
    def: 'Halaman yang menunjukkan setiap balasan yang ditulis oleh AI. Di sini anda boleh meluluskan, menyunting atau menolak balasan yang menunggu semakan.',
  },
  draft: {
    term: 'Draf',
    def: 'Balasan yang telah ditulis oleh AI tetapi belum dihantar. Bergantung pada tetapan anda, manusia mungkin perlu meluluskannya dahulu.',
  },
  persona: {
    term: 'Persona',
    def: 'Personaliti dan gaya bercakap AI, seperti mesra dan ringkas. Anda menetapkannya di “Persona & peraturan”.',
  },
  houseRules: {
    term: 'Peraturan rumah',
    def: 'Peraturan ringkas yang mesti sentiasa dipatuhi AI, satu setiap baris, seperti “Jangan sekali-kali menjanjikan bayaran balik”. Cawangan boleh menambah peraturan tetapi tidak boleh membuang peraturan ibu pejabatnya.',
  },
  knowledge: {
    term: 'Pangkalan pengetahuan',
    def: 'Fakta, soalan lazim dan polisi yang dibaca AI sebelum menjawab. Pastikan ia sentiasa terkini supaya AI menjawab dengan betul.',
  },
  lesson: {
    term: 'Pengajaran',
    def: 'Sesuatu yang dipelajari AI daripada maklum balas, seperti pembetulan kepada jawapan yang salah. Pengajaran boleh menunggu kelulusan manusia sebelum AI menggunakannya.',
  },
  skill: {
    term: 'Kemahiran AI',
    def: 'Kebolehan atau arahan tambahan untuk AI, seperti membuat temu janji atau menyemak balasan sebelum dihantar. Uruskannya di “Kemahiran AI”.',
  },
  journey: {
    term: 'Perjalanan',
    def: 'Pelan langkah demi langkah yang berjalan sendiri untuk setiap kenalan, seperti mesej alu-aluan, tunggu dua hari, kemudian peringatan. Binanya di “Perjalanan”.',
  },
  automation: {
    term: 'Automasi',
    def: 'Peraturan ringkas “apabila ini berlaku, buat itu” untuk perbualan, seperti menanda setiap sembang baharu yang menyebut harga.',
  },
  template: {
    term: 'Templat',
    def: 'Mesej tersimpan yang boleh anda guna semula. Di WhatsApp, templat yang diluluskan ialah satu-satunya jenis mesej yang boleh dihantar di luar tetingkap 24 jam.',
  },
  window24h: {
    term: 'Tetingkap 24 jam',
    def: 'Tempoh selepas mesej terakhir pelanggan di mana anda boleh menghantar mesej biasa kepada mereka. Apabila ia tutup, anda perlukan templat sehingga mereka menulis semula.',
  },
  optOut: {
    term: 'Tarik diri',
    def: 'Apabila pelanggan meminta untuk berhenti menerima mesej, contohnya dengan menghantar STOP. Mereka boleh menyertai semula dengan menghantar START.',
  },
  sandbox: {
    term: 'Kotak pasir',
    def: 'Mod latihan: semuanya kelihatan sebenar tetapi tiada apa yang keluar dari ChatBranch. Ia digunakan apabila saluran atau AI belum ada kunci.',
  },
  apiKey: {
    term: 'Kunci API',
    def: 'Kod rahsia yang membenarkan program lain menggunakan ChatBranch bagi pihak anda, dengan kebenaran anda. Simpan ia secara rahsia seperti kata laluan.',
  },
  mcp: {
    term: 'MCP',
    def: 'Cara standard bagi pembantu AI, seperti Claude, untuk bersambung dengan ChatBranch dan menggunakan alatnya. Anda menyediakannya dengan kunci API di “Kunci API & MCP”.',
  },
}
