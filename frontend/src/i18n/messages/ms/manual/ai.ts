import type { ai as source } from '../../en/manual/ai'
import type { Dict } from '../../../types'

export const ai: Dict<typeof source> = {
  aiPersona: {
    title: 'Tulis persona dan peraturan rumah AI',
    summary: 'Beritahu AI siapa dirinya, bagaimana ia patut berbunyi dan peraturan yang mesti dipatuhi setiap balasan — untuk seluruh organisasi atau untuk satu cawangan.',
    before: 'Anda perlukan log masuk pentadbir (halaman “Persona & peraturan” memerlukan kebenaran tetapan AI). Sediakan beberapa baris tentang perniagaan anda: namanya, apa yang anda jual dan nada yang anda mahu.',
    steps: {
      s1: {
        title: 'Buka Persona & peraturan',
        body: 'Di bar sisi, buka Pustaka → “Persona & peraturan”. Baris di atas menyatakan tahap yang sedang anda sunting, contohnya “Menyunting untuk organisasi.”',
      },
      s2: {
        title: 'Pilih tahap yang disunting',
        body: 'Gunakan penukar entiti di bahagian atas skrin. “Semua entiti” menyunting seluruh organisasi. Klik nama satu cawangan untuk memberi cawangan itu tetapan sendiri.',
      },
      s3: {
        title: 'Tulis persona',
        body: 'Dalam kotak “Persona”, huraikan siapa AI dan cara ia bercakap. Contohnya: “Anda Aina, pembantu mesra Kedai Bunga Melur, kedai bunga di Shah Alam. Balas dengan mesra dan ringkas, dalam bahasa pelanggan.”',
      },
      s4: {
        title: 'Simpan persona',
        body: 'Klik “Simpan persona”. Perkataan hijau “Disimpan” muncul di sebelah butang, dan label “Digunakan:” di atas kad menamakan tahap yang personanya digunakan.',
      },
      s5: {
        title: 'Beri satu cawangan persona sendiri (pilihan)',
        body: 'Selepas memilih cawangan, hidupkan suis “… mempunyai persona sendiri”. Kotak diisi dengan teks yang diwarisi — ubah dan klik “Simpan persona”. Untuk kembali, matikan suis dan klik “Guna persona yang diwarisi”.',
      },
      s6: {
        title: 'Taip peraturan rumah anda',
        body: 'Di bawah “Peraturan rumah”, taip satu peraturan setiap baris dalam “Peraturan untuk semua cawangan” (atau “Peraturan tambahan untuk …” bagi cawangan). Contohnya: “Jangan nyatakan tarikh penghantaran.” dan “Sentiasa tawarkan panggilan semula untuk aduan.”',
      },
      s7: {
        title: 'Simpan peraturan',
        body: 'Klik “Simpan peraturan rumah”. Bagi cawangan, peraturan yang diwarisi disenaraikan di atas kotak di bawah “Diwarisi — sentiasa aktif di sini”. Peraturan bertambah: cawangan mengekalkan setiap peraturan di atasnya dan hanya boleh menambah.',
      },
      s8: {
        title: 'Semak apa yang AI terima',
        body: 'Tatal ke “Apa yang AI terima”. Ia menunjukkan permulaan setiap gesaan mengikut susunan: peraturan keselamatan terbina dalam, kemudian peraturan rumah anda, kemudian persona.',
      },
      s9: {
        title: 'Cuba',
        body: 'Klik “Cuba di ruang uji” di kanan atas kad itu dan bersembang dengan AI sebagai pelanggan untuk mendengar bunyinya.',
      },
    },
    done: 'Kotak “Apa yang AI terima” menunjukkan peraturan dan persona anda, dan label “Digunakan:” menamakan tahap yang anda simpan.',
    tips: {
      t1: 'Ringkaskan persona kepada satu perenggan. Letakkan fakta seperti waktu operasi dalam Pangkalan pengetahuan, bukan dalam persona.',
      t2: 'Tulis peraturan sebagai buat dan jangan yang jelas. Satu peraturan setiap baris memudahkan bacaan dan perubahan.',
      t3: 'Di tahap organisasi, “Tetapkan semula kepada lalai” mengembalikan persona asal.',
      t4: 'Peraturan keselamatan sentiasa didahulukan dan tidak boleh disunting — persona dan peraturan anda tidak akan dapat mengatasinya.',
    },
    trouble: 'Butang simpan berwarna kelabu: belum ada perubahan, atau peranan anda hanya boleh membaca halaman ini (“Anda boleh membaca tetapi tidak boleh mengubahnya.”). Kotak persona kelabu pada cawangan: hidupkan “… mempunyai persona sendiri” dahulu. Halaman menunjukkan organisasi walaupun anda memilih cawangan: beberapa ditandakan dalam penukar — pilih satu sahaja.',
  },

  aiKnowledge: {
    title: 'Ajar AI tentang perniagaan anda',
    summary: 'Tambah fakta, polisi, soalan lazim dan notis yang AI gunakan untuk menjawab pelanggan, muat naik dokumen, dan uji apa yang AI temui bagi sesuatu soalan.',
    before: 'Anda perlukan log masuk pentadbir. Kumpulkan maklumat asas: waktu operasi, alamat, harga, polisi pemulangan dan soalan biasa. PDF teks senarai harga atau polisi anda juga boleh digunakan.',
    steps: {
      s1: {
        title: 'Buka Pangkalan pengetahuan',
        body: 'Di bar sisi, buka Pustaka → “Pangkalan pengetahuan”. Anda melihat entri anda, dengan penapis mengikut jenis di atas.',
      },
      s2: {
        title: 'Mulakan entri baharu',
        body: 'Klik “Entri baharu”. Borang entri baharu dibuka.',
      },
      s3: {
        title: 'Pilih jenis',
        body: 'Dalam “Jenis”, pilih: “Fakta perniagaan” (sentiasa benar, seperti alamat anda), “Polisi / T&S”, “Soalan lazim” (menambah medan Soalan), “Acara / promosi” atau “Notis segera” (seperti “tutup hari ini untuk pembaikan”).',
      },
      s4: {
        title: 'Pilih untuk siapa ia terpakai',
        body: 'Dalam “Terpakai untuk”, biarkan “Semua entiti (seluruh organisasi)” supaya setiap cawangan menggunakannya, atau pilih satu cawangan supaya hanya pelanggannya mendengarnya.',
      },
      s5: {
        title: 'Tulis tajuk dan kandungan',
        body: 'Taip “Tajuk” (contohnya “Waktu operasi”) dan “Kandungan” (contohnya “Kami dibuka dari 9 pagi hingga 6 petang, Isnin hingga Sabtu.”). Bagi soalan lazim, taip juga soalan mengikut cara pelanggan bertanya.',
      },
      s6: {
        title: 'Tambah tarikh untuk acara dan notis (pilihan)',
        body: 'Isi “Bermula (pilihan)” dan “Tamat (pilihan — ia tamat tempoh sendiri)”. AI mula dan berhenti menyebutnya dengan sendiri — berguna untuk promosi atau cuti umum.',
      },
      s7: {
        title: 'Aktif dan Diterbitkan adalah berbeza',
        body: 'Biarkan “Aktif” ditanda supaya AI boleh menggunakan entri itu. Tandakan “Terbitkan ke Pusat Bantuan” hanya jika pelanggan juga patut membacanya di halaman awam /help anda.',
      },
      s8: {
        title: 'Simpan entri',
        body: 'Klik “Tambah entri”. Anda melihat “Pengetahuan ditambah — AI menggunakannya pada mesej seterusnya.” dan entri menunjukkan lencana “Aktif” (atau “Dijadualkan” jika ia bermula kemudian).',
      },
      s9: {
        title: 'Muat naik dokumen (pilihan)',
        body: 'Klik “Muat naik dokumen”, kemudian seret fail PDF, teks, Markdown atau CSV (sehingga 10 MB) ke dalam kotak. Pilih “Jenis” dan “Terpakai untuk”, kemudian klik “Muat naik”. AI membaca teksnya; fail itu sendiri tidak disimpan.',
      },
      s10: {
        title: 'Uji apa yang AI temui',
        body: 'Tatal ke bawah ke “Uji carian”, taip soalan pelanggan seperti “berapa lama bayaran balik diproses?” dan klik “Uji”. “Apa yang ditemui” menyenaraikan entri yang sepadan, dan “Tepat apa yang diberikan kepada AI” menunjukkan teks yang digunakan untuk menjawab.',
      },
    },
    done: 'Entri anda berada dalam senarai dengan lencana hijau “Aktif”, dan “Uji carian” menemuinya bagi soalan yang sepadan.',
    tips: {
      t1: 'Satu topik bagi setiap entri paling berkesan — “Caj penghantaran” dan “Polisi pemulangan” sebagai dua entri, bukan satu halaman panjang.',
      t2: 'Gunakan “Tag” sebagai petunjuk padanan, seperti “bayaran balik” atau “refund”, supaya perkataan berbeza masih menemui entri itu.',
      t3: 'Untuk menghentikan AI menggunakan entri buat sementara, klik “Matikan” padanya dan bukannya memadamnya.',
      t4: 'Tambah penyedia embedding dalam Tetapan → Kunci & model AI supaya AI memadankan soalan mengikut makna, bukan sekadar perkataan.',
    },
    trouble: '“Tambah entri” kelabu: tajuk dan kandungan kedua-duanya wajib. Muat naik ditolak: PDF imbasan ialah gambar tanpa teks — eksport PDF teks atau tampal teks ke dalam entri baharu. “Uji carian” tidak menemui apa-apa: cuba perkataan pelanggan sendiri, tambah tag, atau tulis entri sebagai soalan lazim.',
  },

  aiPlayground: {
    title: 'Uji AI dengan selamat sebagai pelanggan olok-olok',
    summary: 'Bersembang dengan AI seperti pelanggan, lihat sebab ia menjawab begitu, dan betulkannya — tiada apa-apa dihantar kepada sesiapa, disimpan dalam sembang atau ditempah.',
    before: 'Sesiapa yang boleh melihat Pemantau AI boleh menggunakan ruang uji. Mencuba model lain atau draf persona memerlukan kebenaran “Ubah tingkah laku AI” (biasanya pentadbir).',
    steps: {
      s1: {
        title: 'Buka Ruang uji AI',
        body: 'Di bar sisi, buka Automasi → “Ruang uji AI”. Lencana “Ujian sahaja — tiada apa-apa sampai kepada pelanggan” mengingatkan anda ia selamat. Pada telefon, tukar antara “Persediaan”, “Sembang” dan “Pemeriksa” dengan tab.',
      },
      s2: {
        title: 'Pilih cawangan dan saluran',
        body: 'Dalam “Persediaan”, pilih “Cawangan” (pengetahuan, kemahiran, persona dan peraturan semakannya digunakan) dan “Saluran”, seperti WhatsApp.',
      },
      s3: {
        title: 'Reka seorang pelanggan',
        body: 'Di bawah “Pelanggan”, kekalkan “Rekaan” dan taip “Nama” (contohnya Mei Ling), pilih “Bahasa” dan tambah tag jika perlu. Atau pilih “Kenalan sedia ada” dan cari kenalan sebenar — ia hanya dibaca, tidak pernah diubah.',
      },
      s4: {
        title: 'Atau salin perbualan sebenar (pilihan)',
        body: 'Klik “Mula daripada perbualan sebenar”, cari sembang itu, pilih “Mesej untuk disalin” dan klik “Salin ke ruang uji”. Perbualan sebenar tidak berubah.',
      },
      s5: {
        title: 'Tulis sebagai pelanggan',
        body: 'Taip dalam kotak “Tulis sebagai pelanggan…”, contohnya “Ada hantar ke Pulau Pinang?”, dan klik “Hantar”. Balasan AI muncul selepas seketika.',
      },
      s6: {
        title: 'Baca apa yang akan berlaku dalam produksi',
        body: 'Di bawah setiap balasan, cip memberitahu apa yang peti masuk sebenar akan lakukan: “Akan dihantar”, “Ditahan untuk semakan”, “Serahkan kepada manusia”, “Disekat” atau “AI dimatikan di sini”, berserta sebabnya.',
      },
      s7: {
        title: 'Lihat sebab ia menjawab begitu',
        body: 'Klik “Periksa” di bawah balasan. “Pemeriksa” menunjukkan “Asas jawapannya” (pengetahuan dan pengajaran), alat yang dipanggil dan semakan balasan yang lulus.',
      },
      s8: {
        title: 'Ajar pembetulan',
        body: 'Jika jawapan salah, klik “Sepatutnya berkata…”, taip jawapan yang lebih baik dalam “Apa yang sepatutnya dikatakan” dan klik “Simpan pengajaran”. Bagi jawapan yang bagus, klik “Jawapan yang baik” untuk menyimpannya sebagai contoh.',
      },
      s9: {
        title: 'Cuba lagi atau mula semula',
        body: 'Klik “Jana semula” untuk mendapat balasan baharu bagi mesej yang sama, atau “Set semula sembang” untuk memulakan perbualan baharu (persediaan anda kekal).',
      },
    },
    done: 'Selepas menyimpan pengajaran, anda melihat “Disimpan — menunggu semakan di Pemantau AI → Pembelajaran” atau “Disimpan — AI menggunakannya mulai balasan seterusnya.”',
    tips: {
      t1: 'Uji soalan yang paling kerap ditanya pelanggan, dalam setiap bahasa yang anda layan.',
      t2: 'Pentadbir boleh membuka “Cuba perubahan” untuk menguji model lain atau persona yang belum disimpan, dan “Bandingkan dengan tetapan uji lain” secara bersebelahan.',
      t3: 'Gunakan “Muat turun Markdown” untuk berkongsi perbualan ujian dengan pasukan anda.',
    },
    trouble: 'Lencana “Kotak pasir” bermakna tiada model AI disambungkan, jadi balasan hanya gema — superadmin menambah kunci dalam Tetapan → Kunci & model AI. “Paling banyak … mesej”: klik “Set semula sembang”. Terlalu banyak cubaan dalam seminit: tunggu seminit dan hantar semula.',
  },

  aiTurnOn: {
    title: 'Hidupkan atau matikan AI',
    summary: 'Hidupkan AI untuk seluruh organisasi, satu cawangan, mengikut jadual, satu sembang atau satu kenalan — dan ketahui sebab ia mati bagi sesuatu perbualan.',
    before: 'Mengubah suis utama, jadual dan tag tanpa-AI memerlukan kebenaran “Sunting tetapan tingkah laku AI” (biasanya pentadbir). Suis bagi setiap sembang memerlukan kebenaran membalas sembang.',
    steps: {
      s1: {
        title: 'Cari suis utama',
        body: 'Di bar sisi, buka Automasi → “Pemantau AI”. Kad teratas, “Suis utama AI”, menyatakan “AI HIDUP” atau “AI MATI” untuk skop semasa.',
      },
      s2: {
        title: 'Pilih di mana ia terpakai',
        body: 'Dalam pemilih di sebelah suis, pilih “Seluruh organisasi” atau satu cawangan.',
      },
      s3: {
        title: 'Tekan suis',
        body: 'Klik suis. Anda melihat “AI dihidupkan untuk …” (atau dimatikan). Mati bermakna tiada balasan AI, susulan atau mesej perjalanan AI di mana-mana dalam skop itu.',
      },
      s4: {
        title: 'Jalankan AI mengikut jadual (pilihan)',
        body: 'Klik “Jadual & pengecualian”. Di bawah “AI di …”, pilih “Mengikut jadual”, tambah waktu bagi setiap hari dengan “Waktu”, kemudian pilih “AI semasa waktu ini” atau “AI di luar waktu ini” (contohnya, AI menjaga waktu malam semasa staf bekerja pada waktu pejabat). Klik “Simpan”.',
      },
      s5: {
        title: 'Jauhkan AI daripada sesetengah kenalan',
        body: 'Dalam panel yang sama, gunakan “Jangan guna AI untuk kenalan bertag” untuk memilih tag seperti VIP atau Aduan, kemudian klik “Simpan”.',
      },
      s6: {
        title: 'Tetapkan cawangan secara berbeza (pilihan)',
        body: 'Pilih cawangan dalam pemilih dan pilih modnya sendiri. “Guna tetapan induk” menjadikannya mengikut tahap di atas semula. “Mati” di tahap lebih tinggi sentiasa menang.',
      },
      s7: {
        title: 'Tukar untuk satu sembang',
        body: 'Dalam Kerja → “Sembang”, buka perbualan. Gunakan suis kecil “AI” di kanan atas pengepala sembang. Halakan tetikus padanya untuk membaca sebab AI hidup atau mati.',
      },
      s8: {
        title: 'Tukar untuk satu kenalan',
        body: 'Dalam Kerja → “Kenalan”, buka kenalan. Dalam kad “AI untuk kenalan ini”, pilih “Ikut sembang” (biasa), “Tidak pernah” atau “Sentiasa”.',
      },
      s9: {
        title: 'Ketahui sebab AI mati',
        body: 'Jika suis “AI” sembang kelabu dan tidak boleh diklik, satu peraturan di atasnya yang menentukan — suis utama atau jadual, tag tanpa-AI, kenalan ditetapkan “Tidak pernah”, atau pelanggan telah menarik diri. Petua alat menamakannya; ubah di tempat itu.',
      },
    },
    done: 'Kad suis utama menyatakan “AI HIDUP”, dan suis “AI” dalam pengepala sembang hidup.',
    tips: {
      t1: 'Cip Live di bar atas mempunyai togol pantas hidup/mati untuk suis utama.',
      t2: 'Semasa menyediakan saluran, langkah “siapa yang menjawab” membolehkan peti masuk itu memulakan sembang baharu dengan AI hidup atau mati.',
      t3: 'Balasan yang diluluskan oleh manusia tetap dihantar walaupun AI dimatikan.',
      t4: 'Sembang e-mel tiada suis AI — AI tidak membalas e-mel secara automatik.',
    },
    trouble: 'AI hidup tetapi tidak pernah membalas: AI hanya menjawab perbualan yang dipautkan kepada kenalan — pautkan atau cipta kenalan daripada panel maklumat sembang. Balasan menunggu dalam Pemantau AI: peraturan semakan anda menahannya (lihat “Semak balasan AI sebelum dihantar”). Balasan kelihatan seperti gema: kunci AI belum ditetapkan (tanya superadmin anda).',
  },

  aiReview: {
    title: 'Semak balasan AI sebelum dihantar',
    summary: 'Tentukan balasan AI mana yang disemak oleh manusia dahulu, kemudian luluskan, sunting atau tolak daripada Pemantau AI atau terus di dalam sembang.',
    before: 'Sesiapa yang mempunyai kebenaran “Lihat & luluskan draf AI” boleh menyemak (ejen memilikinya). Mengubah peraturan semakan memerlukan kebenaran “Sunting tetapan tingkah laku AI”.',
    steps: {
      s1: {
        title: 'Buka Pemantau AI',
        body: 'Di bar sisi, buka Automasi → “Pemantau AI”. Baris di bawah tajuk menyatakan peraturan semasa, contohnya “Kini, hanya balasan berisiko ditahan.”',
      },
      s2: {
        title: 'Buka peraturan semakan',
        body: 'Klik tab “Tetapan”. Dalam “Tetapan untuk”, kekalkan “Lalai organisasi (setiap entiti)” atau pilih satu cawangan.',
      },
      s3: {
        title: 'Pilih siapa yang menyemak AI',
        body: 'Dalam “Semak sebelum menghantar” → “Siapa menyemak AI sebelum ia menghantar”, pilih: “Hantar serta-merta” (tiada semakan), “Semak balasan berisiko” (hanya balasan dengan wang, tarikh atau perkataan berisiko anda menunggu), “Semak setiap balasan”, atau “Mod bayang” (AI menulis draf tetapi tidak pernah menghantar — sesuai untuk percubaan).',
      },
      s4: {
        title: 'Tetapkan tarikh akhir (pilihan)',
        body: '“Hantar balasan yang ditahan secara automatik selepas (minit)”: 0 bermakna balasan menunggu manusia; 10 bermakna ia dihantar sendiri selepas 10 minit jika tiada siapa bertindak. Anda juga boleh menambah “Perkataan yang menjadikan balasan berisiko”, seperti bayaran balik, diskaun.',
      },
      s5: {
        title: 'Simpan peraturan',
        body: 'Klik “Simpan perubahan” pada bar di bawah. Contoh “Bagaimana ia berlaku” di sebelah kanan menunjukkan apa yang akan dilakukan oleh peraturan itu.',
      },
      s6: {
        title: 'Buka baris gilir semakan',
        body: 'Klik tab “Menunggu semakan” — nombor padanya ialah bilangan balasan yang menunggu. Setiap kad menunjukkan perbualan dan “Cadangan AI”.',
      },
      s7: {
        title: 'Luluskan balasan yang baik',
        body: 'Klik “Luluskan & hantar” (atau tekan A). Ia dihantar kepada pelanggan melalui saluran biasa.',
      },
      s8: {
        title: 'Sunting balasan',
        body: 'Klik “Sunting” (atau tekan E), ubah teks, kemudian klik “Hantar balasan yang disunting”. “Buat asal suntingan” mengembalikan perkataan AI.',
      },
      s9: {
        title: 'Tolak balasan',
        body: 'Klik “Tolak” (atau tekan R), pilih sebab atau taip dalam “Kenapa ditolak? (pilihan)”, dan sahkan dengan “Tolak”. Tiada apa-apa dihantar, dan sebab itu membantu AI menjadi lebih baik.',
      },
      s10: {
        title: 'Semak dari dalam sembang',
        body: 'Apabila anda membuka sembang dengan balasan yang ditahan, sepanduk “Balasan AI menunggu semakan” menunjukkannya dengan “Luluskan & hantar”, “Tolak” dan “Sunting dalam pemantau”.',
      },
    },
    done: 'Baris gilir menunjukkan “Semua sudah selesai”, dan balasan yang diluluskan muncul dalam sembang sebagai telah dihantar.',
    tips: {
      t1: 'Mulakan AI baharu dalam “Mod bayang” atau “Semak setiap balasan”, kemudian longgarkan kepada “Semak balasan berisiko” apabila anda sudah mempercayainya.',
      t2: 'Tekan J dan K untuk bergerak antara kad; tandakan beberapa kad untuk meluluskan atau menolaknya serentak.',
      t3: 'Tab “Aktiviti AI” menyenaraikan semua yang ditulis AI — dihantar, ditahan, ditolak atau dihentikan.',
      t4: 'Hidupkan “Maklumkan penyemak apabila ada balasan menunggu” supaya penyemak menerima amaran loceng.',
    },
    trouble: 'Mesej “rakan sepasukan sudah membalas dalam sembang ini selepas AI menulisnya”: klik “Hantar juga” hanya jika ia masih sesuai, jika tidak tolaknya. “Tidak dihantar”: pengawal keselamatan menghentikannya — pelanggan telah menarik diri atau tetingkap 24 jam WhatsApp telah ditutup. Tab Tetapan hanya boleh dibaca: minta kebenaran daripada pentadbir.',
  },

  aiTeach: {
    title: 'Ajar AI dengan ibu jari ke atas dan ke bawah',
    summary: 'Nilai balasan AI, nyatakan apa yang sepatutnya dikatakan, dan jadikan maklum balas itu pengajaran yang AI gunakan dalam balasan seterusnya.',
    before: 'Sesiapa yang mempunyai kebenaran “Lihat & luluskan draf AI” boleh menilai balasan. Meluluskan pengajaran dan memilih mod pembelajaran memerlukan kebenaran “Sunting tetapan tingkah laku AI”.',
    steps: {
      s1: {
        title: 'Cari ibu jari pada balasan AI',
        body: 'Dalam Kerja → “Sembang”, buka perbualan dan halakan tetikus pada balasan yang ditulis AI. Pil kecil 👍 / 👎 muncul di sebelahnya. Pada telefon, ketik label “Pembantu AI” di atas balasan.',
      },
      s2: {
        title: 'Ibu jari ke atas untuk balasan baik',
        body: 'Klik 👍 (“Balasan baik”). Satu klik sudah memadai — AI boleh menggunakannya sebagai contoh yang baik.',
      },
      s3: {
        title: 'Ibu jari ke bawah untuk balasan teruk',
        body: 'Klik 👎. Kotak “Apa yang salah dengan balasan ini?” dibuka. Tandakan satu atau lebih sebab, seperti “Maklumat salah” atau “Terlalu panjang”.',
      },
      s4: {
        title: 'Tulis jawapan yang lebih baik',
        body: 'Dalam “Apa yang sepatutnya dikatakan?”, taip balasan yang anda sendiri akan hantar. Tambah “Nota untuk pasukan anda” jika berguna. Pelanggan tidak pernah melihat semua ini.',
      },
      s5: {
        title: 'Hantar maklum balas',
        body: 'Klik “Hantar maklum balas”. Anda melihat “Terima kasih — satu pengajaran sedang menunggu semakan.” (atau AI terus menggunakannya).',
      },
      s6: {
        title: 'Pilih mod pembelajaran',
        body: 'Buka Automasi → “Pemantau AI” → tab “Pembelajaran”. Di bawah “Cara AI belajar”, pilih “Semak dahulu” (manusia meluluskan setiap pengajaran), “Belajar daripada pentadbir” (maklum balas pentadbir terus aktif) atau “Tutup”.',
      },
      s7: {
        title: 'Luluskan cadangan pengajaran',
        body: 'Klik tab “Dicadangkan”. Setiap kad menunjukkan soalan dan jawapan yang lebih baik. Klik “Luluskan” untuk mengaktifkannya, atau “Ketepikan” jika ia salah.',
      },
      s8: {
        title: 'Urus pengajaran aktif',
        body: 'Tab “Aktif” menyenaraikan apa yang AI gunakan sekarang. Klik “Arkibkan” untuk berhenti menggunakannya, atau “Jadikan pengetahuan” untuk menjadikannya soalan lazim dalam pangkalan pengetahuan.',
      },
      s9: {
        title: 'Tambah peraturan secara manual (pilihan)',
        body: 'Klik “Tambah peraturan”, taip peraturan — contohnya “Jangan janjikan penghantaran hari yang sama” — dan klik “Tambah peraturan”. Peraturan sentiasa ada dalam arahan AI.',
      },
    },
    done: 'Pengajaran muncul di bawah tab “Aktif” dan anda melihat “Pengajaran #… kini aktif — AI menggunakannya mulai balasan seterusnya.”',
    tips: {
      t1: 'Ibu jari ke bawah dengan pembetulan bertulis paling banyak mengajar. Sebab sahaja hanya dikira.',
      t2: 'Meluluskan, menyunting dan menolak balasan dalam Pemantau AI juga dikira sebagai maklum balas.',
      t3: 'Pengajaran kekal dengan cawangannya dan cawangan di bawahnya — ia tidak merebak ke cawangan lain yang setara.',
    },
    trouble: 'Tiada cadangan pengajaran muncul: mod pembelajaran mungkin “Tutup”, atau belum ada sesiapa menulis pembetulan. Pengajaran seolah-olah tidak digunakan: pastikan ia berada di bawah “Aktif”, bukan “Dicadangkan”, dan milik cawangan yang betul.',
  },

  aiSkills: {
    title: 'Tambah kemahiran AI dengan wizard',
    summary: 'Huraikan dalam perkataan anda sendiri sesuatu yang patut boleh dilakukan AI, jawab beberapa soalan, semak dan simpan.',
    before: 'Anda perlukan log masuk pentadbir. Fikirkan satu perkara pada satu masa, contohnya “Beritahu pelanggan sama ada kami dibuka sekarang dan bila kami dibuka seterusnya”.',
    steps: {
      s1: {
        title: 'Buka Kemahiran AI',
        body: 'Di bar sisi, buka Pustaka → “Kemahiran AI”. Halaman dibahagikan kepada “Arahan”, “Alat”, “Semakan balasan” dan “Aplikasi bersambung”, serta “Alat terbina dalam” yang sentiasa hidup.',
      },
      s2: {
        title: 'Mulakan kemahiran baharu',
        body: 'Klik “Kemahiran baharu”. Wizard dibuka pada langkah “Huraikan”.',
      },
      s3: {
        title: 'Huraikan apa yang patut dilakukan AI',
        body: 'Dalam “Apa yang patut boleh dilakukan AI?”, tulis dalam bahasa biasa. Anda juga boleh mengklik salah satu contoh di bawah “Cuba:”.',
      },
      s4: {
        title: 'Pilih jenis (atau biar AI pilih)',
        body: 'Di bawah “Jenis kemahiran”, kekalkan “Biar AI tentukan”. “Arahan” ialah panduan dalam bahasa biasa tanpa kod; “Alat” atau “Semakan balasan” ialah kod untuk jawapan yang mesti tepat.',
      },
      s5: {
        title: 'Teruskan dan jawab soalan',
        body: 'Klik “Teruskan”. Jika ada yang kurang jelas, wizard bertanya sehingga empat soalan — jawab dan klik “Teruskan”, atau klik “Langkau — draf sekarang”.',
      },
      s6: {
        title: 'Semak draf',
        body: 'Baca “Nama”, “Guna apabila” (bila AI patut menggunakannya) dan “Arahan”. Ubah mana-mana perkataan — semuanya boleh disunting.',
      },
      s7: {
        title: 'Uji alat dan semakan',
        body: 'Bagi alat atau semakan balasan, klik “Jalankan ujian”. Apabila setiap ujian lulus, “Simpan & hidupkan” boleh diklik.',
      },
      s8: {
        title: 'Simpan',
        body: 'Klik “Simpan” (arahan) atau “Simpan & hidupkan” (kod). Kemahiran muncul dalam bahagiannya dengan suis hidup/mati.',
      },
      s9: {
        title: 'Cuba di ruang uji',
        body: 'Buka Automasi → “Ruang uji AI” dan tanya soalan yang patut menggunakan kemahiran itu. “Periksa” menunjukkan alat yang dipanggil.',
      },
    },
    done: 'Anda melihat “… disimpan — AI menggunakannya mulai mesej seterusnya.” dan kemahiran disenaraikan dengan suisnya hidup.',
    tips: {
      t1: 'Arahan seperti nota kepada pekerja baharu. Aplikasi bersambung membolehkan AI menggunakan sistem lain — kini, kalendar untuk menempah temujanji.',
      t2: 'Baru bermula? Klik “Muatkan arahan permulaan” dalam bahagian Arahan yang kosong untuk contoh siap sedia.',
      t3: 'Alat terbina dalam sudah menjawab soalan masa dan tarikh dengan tepat — tiada kemahiran diperlukan untuk itu.',
      t4: 'Untuk menjeda kemahiran, matikan suisnya dan bukannya memadamnya.',
    },
    trouble: '“Teruskan” kelabu: tulis huraian yang lebih panjang sedikit. “Simpan & hidupkan” kelabu: ada ujian yang gagal — kembali, betulkan kod atau ujian, dan jalankan semula, atau klik “Simpan juga dalam keadaan dimatikan”. Tanpa kunci AI, wizard membuat draf daripada pustaka templat.',
  },

  aiCalendar: {
    title: 'Benarkan AI menempah temujanji',
    summary: 'Tambah kemahiran kalendar supaya AI boleh menawarkan masa lapang, menempah dan membatalkan temujanji — dalam kalendar ChatBranch sendiri atau di Google Calendar.',
    before: 'Anda perlukan log masuk pentadbir. Untuk kalendar ChatBranch sendiri, tiada apa-apa lagi diperlukan. Untuk Google Calendar, sambungkannya dahulu di Saluran → Integrasi (dikongsi), atau setiap ejen menyambung kalendar sendiri di Tetapan → Sambungan saya.',
    steps: {
      s1: {
        title: 'Buka Kemahiran AI',
        body: 'Di bar sisi, buka Pustaka → “Kemahiran AI” dan klik “Kemahiran baharu” (atau “Sediakan tempahan kalendar” dalam bahagian Aplikasi bersambung yang kosong).',
      },
      s2: {
        title: 'Pilih aplikasi bersambung',
        body: 'Dalam wizard, klik baris “Aplikasi bersambung” di bawah “Jenis kemahiran” → “Sediakan”. Tiada huraian diperlukan.',
      },
      s3: {
        title: 'Pilih kalendar',
        body: 'Di bawah “Kalendar”, pilih “Tempahan ChatBranch” (berfungsi tanpa Google — tempahan disimpan di halaman Tempahan) atau “Google Calendar” (ia menunjukkan “Disambungkan” atau belum disambungkan).',
      },
      s4: {
        title: 'Untuk Google: kalendar siapa',
        body: 'Dalam “Kalendar siapa”, pilih “Ejen yang ditugaskan untuk perbualan”, “Kalendar dikongsi cawangan ini” atau satu sambungan tertentu. Biarkan “ID kalendar” sebagai primary untuk kalendar utama akaun.',
      },
      s5: {
        title: 'Pilih apa yang boleh dilakukan AI',
        body: 'Di bawah “Apa yang boleh dilakukan AI”, tandakan “Semak kekosongan”, “Tempah temujanji” dan, jika mahu, “Batal / jadual semula”.',
      },
      s6: {
        title: 'Tetapkan waktu bekerja anda',
        body: 'Di bawah “Waktu bekerja”, tukar setiap hari kepada dibuka atau ditutup dan taip masa dalam format 24 jam, contohnya 09:00 hingga 18:00. Gunakan “Tambah rehat” untuk waktu makan tengah hari.',
      },
      s7: {
        title: 'Tetapkan peraturan temujanji',
        body: 'Di bawah “Temujanji”, tetapkan “Tempoh temujanji” (contohnya 30 minit), “Penimbal di sekeliling masa sibuk”, “Tempah sehingga” berapa hari ke hadapan, “Notis minimum” dan “Tempahan aktif bagi setiap pelanggan”.',
      },
      s8: {
        title: 'Namakan acara kalendar',
        body: 'Di bawah “Acara kalendar”, taip “Tajuk acara”. Gunakan butang Sisip untuk menambah nama atau telefon pelanggan.',
      },
      s9: {
        title: 'Pratonton masa lapang',
        body: 'Klik “Pratonton kekosongan”. Anda melihat masa yang akan ditawarkan AI sekarang — larian percubaan, tiada apa-apa ditempah.',
      },
      s10: {
        title: 'Simpan kemahiran',
        body: 'Klik “Simpan”. Kemahiran muncul di bawah “Aplikasi bersambung” dengan suisnya hidup.',
      },
      s11: {
        title: 'Lihat tempahan',
        body: 'Buka Kerja → “Tempahan” untuk melihat setiap temujanji. Ia juga dipaparkan di halaman kenalan dan dalam panel maklumat sembang. Tempahan berstatus “Dikhaskan” semasa balasan sedang dihantar dan “Ditempah” sebaik sahaja ia dihantar.',
      },
    },
    done: 'Apabila pelanggan menempah dalam sembang, temujanji dipaparkan sebagai “Ditempah” di halaman Tempahan dan nota 📅 muncul dalam perbualan.',
    tips: {
      t1: 'Uji dalam Ruang uji AI dahulu — alat kalendar hanya membuat pratonton di sana, tiada apa-apa ditempah.',
      t2: 'Jika balasan ditahan untuk semakan, tempahan hanya dibuat apabila seseorang meluluskan balasan itu.',
      t3: 'Anda boleh membatalkan tempahan dari halaman Tempahan — pelanggan tidak dimaklumkan secara automatik, jadi beritahu mereka dalam sembang.',
    },
    trouble: 'Google menunjukkan belum disambungkan: sambungkannya di Integrasi — sehingga itu AI memberitahu kalendar tidak tersedia dan menawarkan rakan sepasukan; ia tidak pernah menempah dalam ChatBranch sebagai ganti. “Tiada masa lapang”: luaskan waktu bekerja, pendekkan notis minimum atau benarkan lebih banyak hari ke hadapan. Tempahan gagal semasa balasan dihantar: balasan ditahan dan sembang diserahkan kepada manusia.',
  },

  aiHumanLike: {
    title: 'Jadikan balasan AI terasa seperti manusia',
    summary: 'Biarkan AI menunggu pelanggan selesai, menunjukkan status menaip, memecahkan jawapan panjang kepada mesej pendek dan membuat susulan apabila pelanggan diam.',
    before: 'Anda perlukan kebenaran “Sunting tetapan tingkah laku AI” (biasanya pentadbir).',
    steps: {
      s1: {
        title: 'Buka tetapan AI',
        body: 'Di bar sisi, buka Automasi → “Pemantau AI” dan klik tab “Tetapan”.',
      },
      s2: {
        title: 'Pilih tahap',
        body: 'Dalam “Tetapan untuk”, kekalkan “Lalai organisasi (setiap entiti)” atau pilih satu cawangan untuk mengubahnya di sana sahaja.',
      },
      s3: {
        title: 'Tunggu pelanggan selesai',
        body: 'Di bawah “Tunggu pelanggan selesai”, tetapkan “Tunggu pelanggan selesai (saat)” — contohnya 20 — supaya beberapa mesej pendek mendapat satu jawapan. Tetapkan “…tetapi jangan tunggu lebih lama daripada (saat)”, contohnya 90.',
      },
      s4: {
        title: 'Jangan balas “ok, terima kasih”',
        body: 'Hidupkan “Akuan terima tidak perlu dibalas”. Secara pilihan, taip akuan ringkas seperti “Baik, terima kasih!” untuk dihantar dan bukannya berdiam diri.',
      },
      s5: {
        title: 'Menaip seperti manusia',
        body: 'Di bawah “Menaip seperti manusia”, hidupkan “Tunjukkan status menaip dan berhenti seketika sebelum menghantar”. “Kelajuan menaip (aksara sesaat)” sebanyak 8 terasa seperti penaip yang pantas.',
      },
      s6: {
        title: 'Pecahkan balasan panjang',
        body: 'Di bawah “Pecahkan balasan panjang”, pilih “Pecahkan balasan yang melebihi had” atau “Sentiasa pecahkan pada perenggan”. Tidak lebih daripada 3 mesej, tidak pernah di tengah ayat.',
      },
      s7: {
        title: 'Susulan apabila pelanggan diam',
        body: 'Hidupkan “Susulan apabila pelanggan diam”. Tetapkan “Susulan pertama selepas (jam)”, contohnya 4, dan “Bilangan susulan maksimum bagi setiap kesunyian”, contohnya 2. Untuk WhatsApp, pilih “Templat penglibatan semula untuk tetingkap WhatsApp yang ditutup”.',
      },
      s8: {
        title: 'Lihat contoh',
        body: 'Panel “Bagaimana ia berlaku” di sebelah kanan memainkan semula perbualan contoh dengan tetapan anda dan dikemas kini apabila anda mengubahnya.',
      },
      s9: {
        title: 'Simpan',
        body: 'Klik “Simpan perubahan” pada bar di bawah. Pada cawangan, “Guna lalai” di sebelah medan mengembalikannya kepada nilai organisasi.',
      },
    },
    done: 'Contoh “Bagaimana ia berlaku” menunjukkan masa menunggu, status menaip dan mesej berpecah yang anda pilih, dan bar mengesahkan simpanan.',
    tips: {
      t1: 'Status menaip hanya dipaparkan jika saluran menyokongnya (WhatsApp, Telegram, Messenger dan Instagram); di tempat lain AI hanya berhenti seketika.',
      t2: 'Susulan hanya bermula selepas AI bertanya sesuatu, dan berhenti sebaik sahaja pelanggan membalas, menarik diri, sembang diselesaikan atau manusia mengambil alih.',
      t3: 'Tambah “Panduan untuk mesej susulan”, seperti “Ringkaskan kepada satu baris mesra”, untuk menetapkan nadanya.',
    },
    trouble: 'Nota mengatakan susulan dilangkau: tetingkap 24 jam WhatsApp telah ditutup dan tiada templat penglibatan semula ditetapkan — pilih satu. Balasan terasa lambat: kurangkan saat menunggu. Tetapan hanya boleh dibaca: minta kebenaran daripada pentadbir.',
  },

  aiKeys: {
    title: 'Sambungkan model AI (Kunci & model AI)',
    summary: 'Tampal kunci daripada penyedia AI supaya AI menulis balasan sebenar, pilih model, dan hidupkan carian berasaskan makna.',
    before: 'Hanya superadmin boleh melakukannya (kebenaran kunci AI). Cipta kunci API dahulu di laman web penyedia — OpenRouter, OpenAI atau Google AI Studio (Gemini) — dan sediakan untuk ditampal.',
    steps: {
      s1: {
        title: 'Buka Kunci & model AI',
        body: 'Klik “Tetapan” di bahagian bawah bar sisi. Dalam bahagian AI, buka “Kunci & model AI”.',
      },
      s2: {
        title: 'Tampal kunci anda',
        body: 'Dalam “Kunci penyedia, penyedia sembang & model”, tampal kunci anda ke dalam “Kunci API OpenRouter”, “Kunci API OpenAI” atau “Kunci API Google Gemini”. Satu kunci sudah memadai.',
      },
      s3: {
        title: 'Biarkan penyedia pada Automatik',
        body: 'Dalam “Penyedia AI sembang”, kekalkan “Automatik (susunan keutamaan)” — AI menggunakan penyedia pertama yang mempunyai kunci. Pilih satu penyedia sahaja jika anda mahu yang itu sahaja.',
      },
      s4: {
        title: 'Pilih model (pilihan)',
        body: 'Dalam “Model sembang”, kekalkan “Lalai penyedia”, pilih model daripada senarai, atau pilih “Tersuai…” dan taip id model seperti openai/gpt-4o-mini.',
      },
      s5: {
        title: 'Simpan',
        body: 'Klik “Simpan perubahan” di bahagian atas halaman. Status bertukar daripada “Balasan AI dimatikan — tiada kunci penyedia” kepada “Balasan AI sedia”.',
      },
      s6: {
        title: 'Fahami maksud “kotak pasir”',
        body: 'Tanpa kunci, AI berjalan dalam mod kotak pasir: jawapan terbina dalam yang ringkas atau gema, tidak pernah model sebenar. Ruang uji AI menunjukkan lencana “Kotak pasir” sehingga kunci berfungsi.',
      },
      s7: {
        title: 'Hidupkan carian berasaskan makna (pilihan)',
        body: 'Dalam “Embedding (carian semantik)”, kekalkan “Penyedia” pada “Automatik” (ia menggunakan kunci OpenAI atau Gemini anda) supaya “boleh saya dapat duit balik?” masih menemui polisi bayaran balik anda. “Tutup (carian kata kunci)” memadankan perkataan sahaja.',
      },
      s8: {
        title: 'Uji',
        body: 'Buka Automasi → “Ruang uji AI” dan hantar mesej. Balasan sebenar tanpa lencana “Kotak pasir” bermakna model telah disambungkan.',
      },
    },
    done: 'Halaman menyatakan “Balasan AI sedia” dan Ruang uji AI menjawab tanpa lencana “Kotak pasir”.',
    tips: {
      t1: 'Kunci yang disimpan disembunyikan dan tidak dipaparkan lagi — simpan salinan dalam pengurus kata laluan anda.',
      t2: 'OpenRouter memberi anda banyak model dengan satu kunci; Gemini mempunyai peringkat percuma yang sesuai untuk ujian.',
      t3: 'Kos AI dipaparkan di halaman Bil AI (Semakan → Bil AI).',
      t4: 'Panggilan suara langsung membaca kuncinya daripada fail tetapan ejen suara sendiri, bukan daripada halaman ini.',
    },
    trouble: 'Masih dalam kotak pasir selepas menyimpan: kunci mungkin salah atau kehabisan kredit, atau penyedia yang dipilih gagal — penyedia yang dipilih tidak pernah beralih kepada yang lain. Tetapkan “Penyedia AI sembang” kepada Automatik dan semak kunci di laman web penyedia.',
  },

  aiVoice: {
    title: 'Pilih suara AI untuk panggilan',
    summary: 'Layari dan dengar suara untuk panggilan telefon dan WhatsApp AI, tambah suara anda sendiri, dan ajar suara itu menyebut nama yang sukar.',
    before: 'Anda perlukan log masuk pentadbir. Menambah, mengklon dan mendengar pratonton suara, serta menyegerakkan sebutan, memerlukan kunci ElevenLabs yang disediakan oleh pasukan teknikal anda.',
    steps: {
      s1: {
        title: 'Buka Suara & panggilan',
        body: 'Di bar sisi, buka Automasi → “Suara & panggilan”. Empat kad di atas menerangkan personaliti panggilan, seperti “Penasihat Mesra”.',
      },
      s2: {
        title: 'Tapis pustaka suara',
        body: 'Dalam “Pustaka Suara”, gunakan penapis (jantina, bahasa, loghat, sumber, persona) untuk mengecilkan senarai. “Kosongkan penapis” menunjukkan semuanya semula.',
      },
      s3: {
        title: 'Dengar suara',
        body: 'Klik butang main pada kad suara (“Main pratonton”). Ia menyebut ayat contoh dalam bahasanya sendiri — buka “Ayat pratonton contoh” untuk membacanya.',
      },
      s4: {
        title: 'Pilih suara',
        body: 'Klik suara bertanda “Sedia” untuk memilihnya bagi kempen panggilan yang anda lancarkan. Untuk satu panggilan, pilih “Suara ejen” semasa memulakan panggilan daripada sembang.',
      },
      s5: {
        title: 'Tambah suara anda sendiri (pilihan)',
        body: 'Klik “+ Tambah suara”. Di bawah “Tambah suara anda sendiri”, klik “Muat naik fail” (MP3 atau WAV, 30–60 saat pertuturan yang jelas) atau “Rakam” dan baca skrip yang dipaparkan. Beri nama apabila diminta.',
      },
      s6: {
        title: 'Atau import suara ElevenLabs (pilihan)',
        body: 'Dalam tetingkap yang sama, di bawah “atau import suara ElevenLabs sedia ada”, tampal “ID suara ElevenLabs” dan klik “Import”.',
      },
      s7: {
        title: 'Betulkan cara sesuatu perkataan disebut',
        body: 'Tatal ke “Sebutan” dan klik “Tambah istilah”. Taip perkataan dalam “Istilah” (contohnya Northwind) dan bunyinya dalam “Ejaan semula”, menggunakan huruf biasa (contohnya Nort-wind).',
      },
      s8: {
        title: 'Semak dan simpan',
        body: 'Gunakan butang pembesar suara untuk mendengar pelayar anda membaca istilah dan ejaan semula, kemudian klik “Simpan & segerakkan”.',
      },
    },
    done: 'Anda melihat “Disimpan dan disegerakkan ke ElevenLabs.” dan suara anda muncul dalam pustaka bertanda “Sedia”.',
    tips: {
      t1: 'Pembetulan sebutan hanya berfungsi untuk suara ElevenLabs — suara OpenAI sentiasa membaca perkataan seperti dieja.',
      t2: 'Klon suara hanya dengan persetujuan penutur.',
      t3: 'Klik ikon pensel pada kad suara untuk menamakan semula atau menukar bahasa atau personanya.',
    },
    trouble: '“Klon gagal” atau “Import gagal”: kunci ElevenLabs tiada atau slot suara anda penuh — tanya pasukan teknikal anda. “Akses mikrofon tidak tersedia”: benarkan mikrofon dalam pelayar anda dan cuba lagi. “Disimpan. Belum disegerakkan ke ElevenLabs”: panggilan sudah menggunakan ejaan semula baharu; segerakkan semula kemudian.',
  },
}
