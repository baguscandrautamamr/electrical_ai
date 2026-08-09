/**
 * Curated grounding notes for standards mode.
 *
 * Not an answer engine and not a copy of the standards: PUIL, SNI and IEC are
 * published documents that cannot be reproduced here, and a table of numbers
 * maintained by hand goes stale. What this is, is the short list of things the
 * model must not get wrong — which standard governs a topic, what it is called,
 * and the figures an engineer is likeliest to ask for.
 *
 * The matching entries are handed to Claude with the question, so an answer is
 * built on something written down rather than on recall. That is the whole
 * point: a chatbot that invents a clause number is worse than one that says it
 * does not know, because an invented "PUIL 2011 pasal 4.12" gets copied onto a
 * drawing and nobody checks it again.
 *
 * `clause` is therefore optional and left out wherever the exact numbering is
 * not certain. An entry naming only the standard is useful; one naming the
 * wrong clause is not.
 */

export interface StandardReference {
  id: string;
  /** The document, as it should be cited. */
  standard: string;
  /** Clause/section, only where it is certain. */
  clause?: string;
  title: { id: string; en: string };
  summary: { id: string; en: string };
  /** Figures worth quoting verbatim. */
  values?: Array<{ label: string; value: string }>;
  /** Lowercase words that should pull this entry in. */
  keywords: string[];
}

export const STANDARD_REFERENCES: StandardReference[] = [
  {
    id: 'puil-2011',
    standard: 'PUIL 2011 (SNI 0225:2011 + Amd 1:2013)',
    title: {
      id: 'Persyaratan Umum Instalasi Listrik',
      en: 'Indonesian general requirements for electrical installations',
    },
    summary: {
      id: 'Standar wajib untuk instalasi listrik tegangan rendah di Indonesia, mengadopsi seri IEC 60364. Berlaku untuk instalasi bangunan sampai 1000 V AC.',
      en: 'The governing standard for low-voltage electrical installations in Indonesia, adopted from the IEC 60364 series. Covers building installations up to 1000 V AC.',
    },
    // Not "standar"/"standard": almost every question contains one of those
    // words, and this entry answered all of them.
    keywords: ['puil', 'sni 0225', '0225', 'instalasi', 'installation', 'instalasi listrik'],
  },
  {
    id: 'puil-kotak-kontak',
    standard: 'PUIL 2011',
    title: {
      id: 'Pemasangan kotak kontak (stop kontak)',
      en: 'Socket outlet installation',
    },
    summary: {
      id: 'Kotak kontak fasa tunggal wajib punya kontak proteksi (pembumian/PE). Di ruangan yang dapat dijangkau anak, kotak kontak dipasang minimal 1,25 m di atas lantai kecuali memakai tipe berpengaman (shuttered). Praktik lazim di gedung memasangnya pada 300 mm (di bawah meja) atau 1.100–1.500 mm (di atas meja) — angka praktik, bukan angka standar.',
      en: 'Single-phase socket outlets must have a protective earth contact. In rooms accessible to small children they are mounted at least 1.25 m above the floor unless a shuttered (child-protected) type is used. Common building practice is 300 mm (below desk) or 1,100–1,500 mm (above desk) — practice figures, not standard ones.',
    },
    values: [
      { label: 'Tinggi min. tanpa pengaman anak', value: '1,25 m' },
      { label: 'Kontak proteksi (PE)', value: 'wajib' },
    ],
    keywords: [
      'kotak kontak', 'stop kontak', 'stopkontak', 'socket', 'outlet', 'receptacle',
      'colokan', 'tinggi', 'height', 'pasang', 'install',
    ],
  },
  {
    id: 'puil-rcd-30ma',
    standard: 'PUIL 2011 / IEC 60364-4-41',
    clause: 'IEC 60364-4-41 §411.3.3',
    title: {
      id: 'GPAS/RCD 30 mA sebagai proteksi tambahan',
      en: 'Additional protection by 30 mA RCD',
    },
    summary: {
      id: 'Gawai proteksi arus sisa (GPAS/RCD) dengan arus sisa pengenal maksimum 30 mA diwajibkan sebagai proteksi tambahan pada kotak kontak untuk penggunaan umum oleh orang awam, dan pada area basah seperti kamar mandi dan dapur.',
      en: 'A residual current device rated at no more than 30 mA is required as additional protection on socket outlets for general use by ordinary persons, and in wet areas such as bathrooms and kitchens.',
    },
    values: [{ label: 'Arus sisa pengenal', value: '≤ 30 mA' }],
    // Not the bare "proteksi"/"protection": every protection topic in the trade
    // uses the word, and lightning questions were landing here.
    keywords: [
      'rcd', 'gpas', 'elcb', 'rcbo', 'arus sisa', 'arus bocor', 'residual',
      'bocor', 'leakage', 'kamar mandi', 'bathroom', 'proteksi tambahan',
      'additional protection', '30 ma',
    ],
  },
  {
    id: 'puil-warna-penghantar',
    standard: 'PUIL 2011',
    title: {
      id: 'Warna inti penghantar',
      en: 'Conductor core colours',
    },
    summary: {
      id: 'Penghantar proteksi (PE) memakai kombinasi hijau-kuning dan warna itu tidak boleh dipakai untuk fungsi lain. Netral memakai biru. Penghantar fasa memakai hitam, coklat dan abu-abu. Urutan L1/L2/L3 terhadap ketiga warna itu sebaiknya dicek langsung di tabel PUIL 2011 sebelum dipakai di gambar kerja.',
      en: 'The protective conductor (PE) uses the green-yellow combination, which must not be used for anything else. Neutral is blue. Phase conductors use black, brown and grey. Which of the three maps to L1/L2/L3 should be checked against the PUIL 2011 table itself before it goes on a drawing.',
    },
    values: [
      { label: 'PE', value: 'hijau-kuning' },
      { label: 'Netral (N)', value: 'biru' },
      { label: 'Fasa', value: 'hitam, coklat, abu-abu' },
    ],
    // Not the bare "pe": it used to be matched as a substring, so it pulled
    // this entry into every question containing "petir", "speaker",
    // "pemasangan" or "penerangan". Not "kabel"/"cable" either — those belong
    // to whichever entry the question is actually about, and a cable-tray
    // question was landing on conductor colours.
    keywords: [
      'warna', 'warna kabel', 'kode warna', 'colour', 'color', 'colour code',
      'penghantar', 'conductor', 'netral', 'neutral', 'fasa', 'phase',
      'hijau kuning', 'arde',
    ],
  },
  {
    id: 'puil-kha',
    standard: 'PUIL 2011',
    title: {
      id: 'Kemampuan hantar arus (KHA) dan koordinasi proteksi',
      en: 'Current-carrying capacity and protection co-ordination',
    },
    summary: {
      id: 'Aturan dasarnya Ib ≤ In ≤ Iz: arus desain ≤ arus pengenal pengaman ≤ KHA penghantar. KHA tabel masih harus dikalikan faktor koreksi suhu ambien, cara pemasangan, dan jumlah sirkit yang berdempetan. Nilai KHA per ukuran kabel harus diambil dari tabel PUIL 2011 yang berlaku, bukan dari hafalan.',
      en: 'The base rule is Ib ≤ In ≤ Iz: design current ≤ protective device rating ≤ conductor capacity. Tabulated capacity is then derated for ambient temperature, installation method and grouping. The per-size figures must come from the PUIL 2011 tables themselves, not from recall.',
    },
    keywords: [
      'kha', 'ampacity', 'kemampuan hantar', 'current carrying', 'nyy', 'nym', 'nya',
      'ukuran kabel', 'cable size', 'penampang', 'mcb', 'mccb', 'derating', 'faktor koreksi',
    ],
  },
  {
    id: 'puil-pembumian',
    standard: 'PUIL 2011 / IEC 60364-5-54',
    title: {
      id: 'Sistem pembumian',
      en: 'Earthing systems',
    },
    summary: {
      id: 'PUIL 2011 mengenal sistem TN (TN-S, TN-C, TN-C-S), TT dan IT. Untuk instalasi bangunan umum di Indonesia yang disuplai PLN, TN-C-S dan TT adalah yang lazim. Nilai resistans pembumian yang dipersyaratkan bergantung pada sistem dan jenis proteksi — angkanya diambil dari PUIL, bukan diseragamkan.',
      en: 'PUIL 2011 recognises TN (TN-S, TN-C, TN-C-S), TT and IT systems. For ordinary buildings on the Indonesian utility supply, TN-C-S and TT are the usual ones. The required earth resistance depends on the system and the protection used — take the figure from PUIL rather than assuming one number fits.',
    },
    keywords: [
      'pembumian', 'grounding', 'earthing', 'arde', 'tn', 'tt', 'it', 'tn-s', 'tn-c-s',
      'elektrode', 'electrode', 'resistans', 'resistance', 'ohm',
    ],
  },
  {
    id: 'iec-60364',
    standard: 'IEC 60364',
    title: {
      id: 'Instalasi listrik tegangan rendah',
      en: 'Low-voltage electrical installations',
    },
    summary: {
      id: 'Seri induk yang diadopsi PUIL 2011. Bagian yang paling sering dirujuk: -4-41 proteksi terhadap kejut listrik, -4-43 proteksi arus lebih, -5-52 sistem perkawatan dan KHA, -5-54 pembumian dan penghantar proteksi, -7-7xx lokasi khusus.',
      en: 'The parent series PUIL 2011 adopts. Most-cited parts: -4-41 protection against electric shock, -4-43 overcurrent protection, -5-52 wiring systems and capacity, -5-54 earthing and protective conductors, -7-7xx special locations.',
    },
    keywords: ['iec 60364', '60364', 'iec', 'low voltage', 'tegangan rendah'],
  },
  {
    id: 'iec-60529',
    standard: 'IEC 60529',
    title: { id: 'Kode IP (tingkat proteksi selungkup)', en: 'IP code (enclosure protection)' },
    summary: {
      id: 'Dua digit: digit pertama proteksi benda padat/debu (0–6), digit kedua proteksi air (0–8, plus 9K). IP54 = terlindung debu + percikan air; IP65 = kedap debu + semprotan air; IP66/67 lazim untuk panel luar ruang. Pilih dari kondisi pemasangan, bukan dari kebiasaan.',
      en: 'Two digits: the first is solids/dust ingress (0–6), the second water (0–8, plus 9K). IP54 = dust-protected plus splashing; IP65 = dust-tight plus water jets; IP66/67 is usual for outdoor enclosures. Choose from the installation condition, not from habit.',
    },
    values: [
      { label: 'Digit 1', value: 'benda padat & debu, 0–6' },
      { label: 'Digit 2', value: 'air, 0–8 (+9K)' },
    ],
    keywords: ['ip', 'ip rating', 'ip54', 'ip65', 'ip66', 'ip67', '60529', 'selungkup', 'enclosure', 'outdoor', 'luar ruang'],
  },
  {
    id: 'iec-61439',
    standard: 'IEC 61439',
    title: { id: 'Perlengkapan hubung bagi dan kendali (panel)', en: 'Low-voltage switchgear and controlgear assemblies' },
    summary: {
      id: 'Standar untuk panel rakitan: -1 aturan umum, -2 panel daya (PHB). Mengatur verifikasi desain, kenaikan suhu, ketahanan hubung pendek, jarak bebas dan bentuk pemisahan (Form 1–4). Yang disertifikasi adalah rakitannya, bukan hanya komponennya.',
      en: 'The standard for assembled panels: -1 general rules, -2 power switchgear assemblies. Covers design verification, temperature rise, short-circuit withstand, clearances and separation forms (Form 1–4). What is certified is the assembly, not just its components.',
    },
    keywords: ['61439', 'panel', 'phb', 'switchgear', 'assembly', 'form 4', 'mdp', 'sdp', 'lvmdp'],
  },
  {
    id: 'iec-60947',
    standard: 'IEC 60947',
    title: { id: 'Komponen hubung bagi dan kendali', en: 'Low-voltage switchgear and controlgear' },
    summary: {
      id: 'Standar komponennya, bukan panelnya: -2 pemutus sirkit (MCCB/ACB), -3 sakelar dan pemisah, -4-1 kontaktor dan starter. MCB rumah tangga justru ada di IEC 60898-1, bukan di sini.',
      en: 'The component standard rather than the panel one: -2 circuit-breakers (MCCB/ACB), -3 switches and disconnectors, -4-1 contactors and starters. Domestic MCBs are in IEC 60898-1 instead.',
    },
    keywords: ['60947', '60898', 'mccb', 'acb', 'mcb', 'kontaktor', 'contactor', 'breaker', 'pemutus'],
  },
  {
    id: 'sni-03-6575',
    standard: 'SNI 03-6575-2001',
    title: {
      id: 'Tata cara perancangan sistem pencahayaan buatan pada bangunan gedung',
      en: 'Design of artificial lighting systems in buildings',
    },
    summary: {
      id: 'Sumber tingkat pencahayaan (lux) yang dipakai untuk mendesain penerangan ruangan di Indonesia, lengkap dengan kelompok renderansi warna dan temperatur warna yang dianjurkan per jenis ruang.',
      en: 'The source of the illuminance (lux) targets used to design room lighting in Indonesia, together with the recommended colour-rendering group and colour temperature per room type.',
    },
    values: [
      { label: 'Ruang kerja / kantor', value: '350 lux' },
      { label: 'Ruang rapat', value: '300 lux' },
      { label: 'Ruang gambar', value: '750 lux' },
      { label: 'Dapur', value: '250 lux' },
      { label: 'Koridor', value: '100 lux' },
      { label: 'Gudang', value: '100 lux' },
    ],
    keywords: [
      'lux', 'pencahayaan', 'lighting', 'illuminance', 'terang', 'lampu', 'luminaire',
      '6575', 'penerangan',
    ],
  },
  {
    id: 'sni-6197',
    standard: 'SNI 6197:2020',
    title: {
      id: 'Konservasi energi pada sistem pencahayaan',
      en: 'Energy conservation in lighting systems',
    },
    summary: {
      id: 'Membatasi daya pencahayaan terpasang per satuan luas (W/m²) menurut fungsi ruang, dan mengatur kendali pencahayaan (saklar zona, sensor hunian, pemanfaatan cahaya alami). Dipakai berdampingan dengan target lux SNI 03-6575: lux menentukan berapa terangnya, SNI 6197 membatasi berapa dayanya.',
      en: 'Caps installed lighting power per unit area (W/m²) by room function, and covers lighting control (zone switching, occupancy sensing, daylight harvesting). Used alongside the SNI 03-6575 lux targets: lux sets how bright, SNI 6197 caps how much power.',
    },
    keywords: ['6197', 'konservasi', 'energy', 'energi', 'w/m2', 'watt', 'daya', 'hemat', 'sensor'],
  },
  {
    id: 'sni-3985',
    standard: 'SNI 03-3985-2000',
    title: {
      id: 'Sistem deteksi dan alarm kebakaran pada bangunan gedung',
      en: 'Fire detection and alarm systems in buildings',
    },
    summary: {
      id: 'Standar Indonesia untuk perancangan, pemasangan dan pengujian sistem deteksi dan alarm kebakaran. Ini padanan lokal dari NFPA 72 yang dipakai perintah /place_fire_alarm; jarak detektor dan titik panggil manual harus diambil dari standar yang benar-benar dipakai proyek, karena keduanya tidak identik.',
      en: 'The Indonesian standard for designing, installing and testing fire detection and alarm systems. It is the local counterpart to the NFPA 72 the /place_fire_alarm command uses; detector spacing and manual call point rules must come from whichever standard the project actually works to, because the two are not identical.',
    },
    // Not "nfpa": that belongs to the NFPA 72 entry. An NFPA question used to
    // be answered with the Indonesian counterpart, which is a different
    // document with different numbers.
    keywords: [
      'kebakaran', 'fire', 'alarm', 'detektor', 'detector', 'smoke', 'asap', 'heat',
      '3985', 'sni 3985', 'sprinkler', 'titik panggil',
    ],
  },
  // ------------------------------------------------------- fire and life safety
  {
    id: 'nfpa-72',
    standard: 'NFPA 72 (National Fire Alarm and Signaling Code)',
    title: {
      id: 'Kode alarm dan pensinyalan kebakaran',
      en: 'Fire alarm and signaling code',
    },
    summary: {
      id: 'Mengatur perancangan, pemasangan, pengujian dan pemeliharaan sistem deteksi dan alarm kebakaran: perangkat inisiasi, perangkat notifikasi, pemantauan, dan jadwal inspeksi. Spasi nominal berlaku untuk langit-langit rata; langit-langit miring, tinggi, atau berbalok serta laju aliran udara tinggi mengubahnya, dan spasi terdaftar (listed spacing) tiap produk mengalahkan angka nominal.',
      en: 'Covers design, installation, testing and maintenance of fire detection and alarm systems: initiating devices, notification appliances, monitoring, and inspection schedules. Nominal spacings apply to smooth ceilings; sloped, high or beamed ceilings and high airflow change them, and a product’s listed spacing overrides the nominal figure.',
    },
    values: [
      { label: 'Spasi nominal detektor asap (langit-langit rata)', value: '30 ft ≈ 9,1 m' },
      { label: 'Spasi terdaftar detektor panas (lazim)', value: '50 ft ≈ 15,2 m' },
      { label: 'Jarak maks detektor ke dinding', value: '0,7 × S' },
      { label: 'Jarak tempuh maks ke titik panggil manual', value: '200 ft ≈ 61 m' },
    ],
    keywords: [
      'nfpa', 'nfpa 72', 'nfpa72', 'fire alarm code', 'detektor asap', 'smoke detector',
      'heat detector', 'detektor panas', 'spasi detektor', 'detector spacing',
      'titik panggil manual', 'manual call point', 'pull station',
      'notification appliance', 'sounder',
    ],
  },
  {
    id: 'en-54',
    standard: 'EN 54',
    title: {
      id: 'Komponen sistem deteksi dan alarm kebakaran',
      en: 'Fire detection and fire alarm system components',
    },
    summary: {
      id: 'Standar produk, bukan standar perancangan: tiap bagian mengatur satu jenis komponen — -2 panel kontrol, -3 sounder, -4 catu daya, -5 detektor panas, -7 detektor asap, -16 panel sound system suara, -23 perangkat notifikasi visual, -24 loudspeaker. Dipakai berdampingan dengan NFPA 72 atau SNI 03-3985 yang mengatur bagaimana komponen itu disusun jadi sistem.',
      en: 'A product standard rather than a design one: each part governs one component type — -2 control panels, -3 sounders, -4 power supplies, -5 heat detectors, -7 smoke detectors, -16 voice alarm control equipment, -23 visual alarm devices, -24 loudspeakers. Used alongside NFPA 72 or SNI 03-3985, which govern how those components become a system.',
    },
    keywords: ['en 54', 'en54', 'sertifikasi detektor', 'komponen alarm', 'voice alarm'],
  },
  {
    id: 'sni-6574',
    standard: 'SNI 03-6574-2001',
    title: {
      id: 'Pencahayaan darurat, tanda arah dan sistem peringatan bahaya',
      en: 'Emergency lighting, exit signage and warning systems',
    },
    summary: {
      id: 'Standar Indonesia untuk pencahayaan darurat pada bangunan gedung: pencahayaan jalur evakuasi saat suplai normal hilang, tanda arah keluar, dan sistem peringatan bahaya. Durasi minimum, tingkat pencahayaan jalur, dan penempatan tanda arah diambil dari dokumen SNI-nya — angka-angka itu bergantung pada klasifikasi bangunan dan tidak seragam.',
      en: 'The Indonesian standard for emergency lighting in buildings: escape-route lighting when the normal supply fails, exit signage, and warning systems. Minimum duration, escape-route illuminance and signage placement come from the SNI document itself — they depend on the building classification and are not one figure.',
    },
    keywords: [
      'pencahayaan darurat', 'emergency lighting', 'lampu darurat', 'tanda arah',
      'exit sign', 'evakuasi', 'escape route', 'jalur keluar', '6574',
    ],
  },
  {
    id: 'iec-60849',
    standard: 'IEC 60849 / EN 54-16 & EN 54-24',
    title: {
      id: 'Sistem suara untuk keperluan darurat',
      en: 'Sound systems for emergency purposes',
    },
    summary: {
      id: 'Mengatur sistem tata suara evakuasi: kejelasan suara terukur, tingkat tekanan suara di atas kebisingan latar, dan pemantauan jalur agar kerusakan kabel atau pengeras suara terdeteksi sebelum dibutuhkan. Kejelasan diukur sebagai STI (Speech Transmission Index); IEC 60268-16 adalah metode ukurnya. EN 54-16 mengatur panelnya dan EN 54-24 pengeras suaranya.',
      en: 'Governs voice evacuation systems: measurable speech intelligibility, sound pressure above background noise, and line monitoring so a failed cable or loudspeaker is found before it is needed. Intelligibility is measured as STI (Speech Transmission Index); IEC 60268-16 is the measurement method. EN 54-16 covers the control equipment and EN 54-24 the loudspeakers.',
    },
    values: [
      { label: 'STI minimum', value: '≥ 0,5' },
      { label: 'Tingkat suara darurat', value: '≥ 65 dB(A) dan ≥ 6 dB di atas kebisingan latar' },
    ],
    keywords: [
      '60849', 'sound system', 'tata suara', 'evakuasi suara', 'voice evacuation',
      'speaker', 'loudspeaker', 'pengeras suara', 'sti', 'kejelasan suara',
      'intelligibility', 'paging',
    ],
  },
  {
    id: 'nfpa-110',
    standard: 'NFPA 110 / NFPA 20',
    title: {
      id: 'Sistem daya darurat dan siaga; pompa kebakaran',
      en: 'Emergency and standby power systems; fire pumps',
    },
    summary: {
      id: 'NFPA 110 mengatur sistem daya darurat: Level 1 untuk beban yang kegagalannya membahayakan nyawa, Level 2 untuk yang tidak; "Type" menyatakan waktu peralihan maksimum yang diizinkan (Type 10 berarti daya tersedia dalam 10 detik). Mencakup pula pengujian beban berkala. NFPA 20 mengatur pemasangan pompa kebakaran, termasuk sirkit dayanya yang tidak boleh diproteksi arus lebih seperti sirkit biasa.',
      en: 'NFPA 110 governs emergency power systems: Level 1 for loads whose failure endangers life, Level 2 for those that do not; the "Type" states the maximum permitted transfer time (Type 10 means power available within 10 seconds). It also covers periodic load testing. NFPA 20 governs fire pump installation, including its supply circuit, which is deliberately not overcurrent-protected like an ordinary one.',
    },
    keywords: [
      'nfpa 110', 'nfpa 20', 'genset', 'generator', 'daya darurat', 'emergency power',
      'standby power', 'ats', 'pompa kebakaran', 'fire pump',
    ],
  },

  // ------------------------------------------------- containment and cabling
  {
    id: 'iec-61537',
    standard: 'IEC 61537',
    title: {
      id: 'Sistem cable tray dan cable ladder',
      en: 'Cable tray systems and cable ladder systems',
    },
    summary: {
      id: 'Standar untuk rak dan tangga kabel beserta penopangnya. Yang mengikat adalah beban kerja aman (SWL) pada jarak tumpuan tertentu — angka itu hasil uji, jadi rak yang sama punya SWL berbeda pada bentang berbeda, dan menambah jarak antar penggantung menurunkannya. Mengatur pula batas lendutan, kelas proteksi korosi, dan kontinuitas listrik bila rak dipakai sebagai penghantar proteksi.',
      en: 'The standard for cable tray and ladder and their supports. What binds is the safe working load (SWL) at a stated support spacing — a tested figure, so the same tray has a different SWL at a different span, and widening the hanger spacing lowers it. It also covers deflection limits, corrosion protection classes, and electrical continuity where the tray serves as a protective conductor.',
    },
    values: [
      { label: 'Batas lendutan uji', value: '1/100 bentang' },
      { label: 'Faktor keamanan uji SWL', value: '1,7' },
    ],
    keywords: [
      '61537', 'cable tray', 'kabel tray', 'rak kabel', 'cable ladder', 'tangga kabel',
      'hanger', 'penggantung', 'tumpuan', 'support spacing', 'jarak penggantung',
      'swl', 'beban kerja aman',
    ],
  },
  {
    id: 'iso-11801',
    standard: 'ISO/IEC 11801 / TIA-568',
    title: {
      id: 'Pengkabelan terstruktur untuk gedung',
      en: 'Generic cabling for customer premises',
    },
    summary: {
      id: 'Standar pengkabelan terstruktur yang dipakai untuk jaringan data dan telepon. Batas keras yang paling sering dilanggar: panjang kanal 100 m, terdiri dari permanent link maksimum 90 m ditambah 10 m kabel patch dan peralatan. Kategori (Cat 5e/6/6A/8) menentukan lebar pita, dan sertifikasi diuji per kanal, bukan per gulung kabel.',
      en: 'The structured cabling standard behind data and telephone outlets. The hard limit most often broken: a 100 m channel, made of a permanent link of at most 90 m plus 10 m of patch and equipment cords. The category (Cat 5e/6/6A/8) sets the bandwidth, and certification is tested per channel, not per drum of cable.',
    },
    values: [
      { label: 'Panjang kanal maks', value: '100 m' },
      { label: 'Permanent link maks', value: '90 m' },
      { label: 'Patch + equipment cord', value: '10 m total' },
    ],
    keywords: [
      '11801', 'tia 568', 'structured cabling', 'pengkabelan terstruktur', 'utp',
      'cat 6', 'cat 6a', 'cat 5e', 'permanent link', 'patch panel', 'outlet data',
      'jaringan data',
    ],
  },
  {
    id: 'ieee-8023-poe',
    standard: 'IEEE 802.3af / 802.3at / 802.3bt',
    title: {
      id: 'Power over Ethernet',
      en: 'Power over Ethernet',
    },
    summary: {
      id: 'Menentukan berapa daya yang boleh dikirim lewat kabel data dan berapa yang benar-benar sampai di perangkat — selisihnya adalah rugi di kabel, dan itulah sebabnya anggaran PoE dihitung dari daya di sisi PSE, bukan dari kebutuhan perangkat. Type 3 dan 4 memberi daya cukup besar sehingga kenaikan suhu pada bundel kabel yang rapat menjadi pertimbangan tersendiri.',
      en: 'Sets how much power may be sent over the data cable and how much actually arrives — the difference is cable loss, which is why a PoE budget is computed at the PSE, not from what the device needs. Types 3 and 4 carry enough power that temperature rise in a tight cable bundle becomes a design consideration of its own.',
    },
    values: [
      { label: 'Type 1 (802.3af)', value: '15,4 W di PSE / 12,95 W di perangkat' },
      { label: 'Type 2 (802.3at)', value: '30 W / 25,5 W' },
      { label: 'Type 3 (802.3bt)', value: '60 W / 51 W' },
      { label: 'Type 4 (802.3bt)', value: '90 W / 71,3 W' },
    ],
    keywords: [
      'poe', 'power over ethernet', '802.3af', '802.3at', '802.3bt', 'anggaran poe',
      'poe budget', 'pse', 'switch port', 'daya switch',
    ],
  },
  {
    id: 'iec-62676',
    standard: 'IEC 62676',
    title: {
      id: 'Sistem video surveillance untuk aplikasi keamanan',
      en: 'Video surveillance systems for use in security applications',
    },
    summary: {
      id: 'Menerjemahkan tujuan pengawasan menjadi angka yang bisa didesain: kepadatan piksel pada objek, bukan resolusi kamera. Kamera 4K yang memandang koridor panjang bisa gagal memenuhi kriteria identifikasi, sementara kamera 2 MP di pintu masuk memenuhinya — yang dihitung adalah piksel per meter di bidang objek. Bagian -4 memberi panduan aplikasi, termasuk kriteria DORI di bawah ini.',
      en: 'Turns a surveillance purpose into a number you can design to: pixel density on the target, not camera resolution. A 4K camera down a long corridor can fail the identification criterion while a 2 MP camera at a doorway meets it — what counts is pixels per metre at the target plane. Part -4 gives the application guidance, including the DORI criteria below.',
    },
    values: [
      { label: 'Detect (deteksi)', value: '25 px/m' },
      { label: 'Observe (observasi)', value: '62 px/m' },
      { label: 'Recognise (pengenalan)', value: '125 px/m' },
      { label: 'Identify (identifikasi)', value: '250 px/m' },
    ],
    keywords: [
      '62676', 'cctv', 'kamera', 'camera', 'video surveillance', 'dori',
      'pengawasan', 'kepadatan piksel', 'pixel density', 'nvr',
    ],
  },

  // -------------------------------------------------------- design and safety
  {
    id: 'iec-60364-7-701',
    standard: 'IEC 60364-7-701',
    title: {
      id: 'Lokasi khusus: kamar mandi dan area pancuran',
      en: 'Special locations: rooms containing a bath or shower',
    },
    summary: {
      id: 'Membagi kamar mandi menjadi zona 0 (di dalam bak/bawah pancuran), zona 1 dan zona 2, dan menentukan apa yang boleh dipasang di masing-masing. Kotak kontak pada umumnya tidak diizinkan di dalam zona, kecuali unit pencukur terisolasi. Peralatan di zona 0 memakai SELV tegangan sangat rendah, dan ikatan penyama potensial tambahan dipersyaratkan pada bagian konduktif terbuka dan asing di ruangan itu.',
      en: 'Divides a bathroom into zone 0 (inside the bath or under the shower), zone 1 and zone 2, and sets what may be installed in each. Socket outlets are generally not permitted inside the zones, apart from isolated shaver units. Equipment in zone 0 runs on extra-low-voltage SELV, and supplementary equipotential bonding is required to the exposed and extraneous conductive parts in the room.',
    },
    keywords: [
      '60364 7 701', 'zona kamar mandi', 'bathroom zone', 'zone 0', 'zone 1', 'zone 2',
      'shower', 'pancuran', 'lokasi khusus', 'special location',
    ],
  },
  {
    id: 'iec-62305',
    standard: 'IEC 62305 / SNI 03-7015-2004',
    title: {
      id: 'Proteksi terhadap sambaran petir',
      en: 'Protection against lightning',
    },
    summary: {
      id: 'IEC 62305 terdiri dari -1 prinsip umum, -2 manajemen risiko, -3 kerusakan fisik dan bahaya jiwa, -4 sistem elektronik di dalam bangunan. Kelas sistem proteksi ditentukan dari hasil penilaian risiko di bagian -2, bukan dipilih langsung — itu langkah yang paling sering dilewati. Radius bola bergulir dipakai untuk menempatkan terminasi udara. SNI 03-7015-2004 adalah standar proteksi petir bangunan gedung di Indonesia.',
      en: 'IEC 62305 comprises -1 general principles, -2 risk management, -3 physical damage and life hazard, -4 electronic systems within structures. The protection class comes out of the part -2 risk assessment rather than being chosen directly — the step most often skipped. The rolling sphere radius positions the air terminations. SNI 03-7015-2004 is the Indonesian building lightning protection standard.',
    },
    values: [
      { label: 'Radius bola bergulir, kelas I–IV', value: '20 m / 30 m / 45 m / 60 m' },
    ],
    keywords: [
      '62305', '7015', 'petir', 'lightning', 'penangkal petir', 'proteksi petir',
      'terminasi udara', 'air termination', 'bola bergulir', 'rolling sphere', 'spd',
    ],
  },
  {
    id: 'iec-61140',
    standard: 'IEC 61140',
    title: {
      id: 'Proteksi terhadap kejut listrik',
      en: 'Protection against electric shock',
    },
    summary: {
      id: 'Standar induk yang mendasari aturan kejut listrik di PUIL dan IEC 60364. Prinsipnya berlapis: proteksi dasar (mencegah sentuh bagian bertegangan) ditambah proteksi gangguan (bekerja saat isolasi dasar gagal), dan keduanya tidak boleh gagal oleh satu sebab yang sama. Kelas peralatan I (bergantung pembumian), II (isolasi ganda), III (SELV) berasal dari sini.',
      en: 'The parent standard behind the shock rules in PUIL and IEC 60364. The principle is layered: basic protection (preventing contact with live parts) plus fault protection (acting when basic insulation fails), and the two must not fail from one common cause. Equipment classes I (relies on earthing), II (double insulation) and III (SELV) come from here.',
    },
    keywords: [
      '61140', 'kejut listrik', 'electric shock', 'kelas i', 'kelas ii', 'class ii',
      'isolasi ganda', 'double insulation', 'selv', 'pelv', 'sentuh langsung',
    ],
  },
  {
    id: 'iec-60364-5-52',
    standard: 'IEC 60364-5-52 / IEC 60287',
    title: {
      id: 'Pemilihan dan pemasangan sistem perkawatan',
      en: 'Selection and erection of wiring systems',
    },
    summary: {
      id: 'Sumber tabel KHA yang dipakai PUIL, beserta metode pemasangan (A1 sampai G) yang menentukan tabel mana yang berlaku — kabel yang sama punya KHA berbeda di konduit tertanam, di rak terbuka, atau ditanam langsung. Faktor koreksi suhu ambien dan pengelompokan sirkit dikalikan setelahnya. IEC 60287 adalah metode perhitungannya bila tidak ada tabel yang cocok.',
      en: 'The source of the capacity tables PUIL uses, together with the installation methods (A1 to G) that decide which table applies — the same cable has a different rating in buried conduit, on open tray, or direct-buried. Ambient temperature and grouping factors are applied afterwards. IEC 60287 is the calculation method for cases no table covers.',
    },
    values: [
      { label: 'Anjuran susut tegangan, pencahayaan', value: '3 %' },
      { label: 'Anjuran susut tegangan, pemakaian lain', value: '5 %' },
    ],
    keywords: [
      '60364 5 52', '60287', 'metode pemasangan', 'installation method', 'konduit',
      'conduit', 'tray terbuka', 'susut tegangan', 'voltage drop', 'drop tegangan',
      'grouping', 'pengelompokan',
    ],
  },
  {
    id: 'iec-60364-6',
    standard: 'IEC 60364-6',
    title: { id: 'Verifikasi dan pengujian instalasi', en: 'Verification of installations' },
    summary: {
      id: 'Mengatur verifikasi awal sebelum instalasi diserahkan: inspeksi visual dulu, baru pengujian, dengan urutan yang tidak boleh dibalik — kontinuitas penghantar proteksi, resistans isolasi, pemisahan SELV/PELV, pemutusan otomatis suplai, polaritas, urutan fasa, uji fungsi. Hasilnya dituangkan sebagai laporan yang menjadi dasar verifikasi berkala berikutnya.',
      en: 'Governs initial verification before an installation is handed over: inspection first, then testing, in an order that must not be reversed — continuity of protective conductors, insulation resistance, SELV/PELV separation, automatic disconnection of supply, polarity, phase sequence, functional tests. The result is a report, which the next periodic verification is measured against.',
    },
    keywords: [
      '60364 6', 'verifikasi', 'verification', 'pengujian', 'testing', 'commissioning',
      'megger', 'resistans isolasi', 'insulation resistance', 'serah terima',
    ],
  },
  {
    id: 'iec-60598',
    standard: 'IEC 60598',
    title: { id: 'Luminer (armatur pencahayaan)', en: 'Luminaires' },
    summary: {
      id: 'Standar produk untuk armatur: -1 persyaratan umum dan pengujian, -2-xx per jenis armatur. Menentukan kelas proteksi kejut, kode IP, batas suhu, dan penandaan pemasangan — termasuk tanda F yang berarti armatur boleh dipasang langsung pada permukaan yang normal mudah terbakar. Armatur tanpa tanda itu butuh jarak dari plafon kayu atau gipsum berlapis.',
      en: 'The product standard for luminaires: -1 general requirements and tests, -2-xx per luminaire type. It sets the shock protection class, IP code, temperature limits and mounting marking — including the F mark, meaning the luminaire may be mounted directly on a normally flammable surface. One without it needs clearance from a timber or lined ceiling.',
    },
    keywords: [
      '60598', 'luminer', 'luminaire', 'armatur', 'downlight', 'fitting lampu',
      'tanda f', 'f mark',
    ],
  },
  {
    id: 'iec-60269',
    standard: 'IEC 60269',
    title: { id: 'Pelebur (sekring) tegangan rendah', en: 'Low-voltage fuses' },
    summary: {
      id: 'Standar pelebur, pelengkap IEC 60947 dan IEC 60898 untuk pemutus sirkit. Kategori pemakaian menyatakan apa yang diproteksi: gG untuk proteksi umum kabel dan peralatan, aM khusus proteksi hubung pendek motor (tidak memproteksi beban lebih, jadi harus dipasangkan relai termal), gM gabungan. Selektivitas antar pelebur lazimnya tercapai pada rasio arus pengenal sekitar 1,6:1.',
      en: 'The fuse standard, the companion to IEC 60947 and IEC 60898 for circuit-breakers. The utilisation category states what is protected: gG for general cable and equipment protection, aM for motor short-circuit protection only (it does not protect against overload, so a thermal relay must go with it), gM for both. Discrimination between fuses is usually achieved at a rating ratio of about 1.6:1.',
    },
    keywords: [
      '60269', 'pelebur', 'sekring', 'fuse', 'gg', 'am', 'nh fuse', 'hrc',
      'selektivitas', 'discrimination',
    ],
  },
  {
    id: 'iec-60034',
    standard: 'IEC 60034',
    title: { id: 'Mesin listrik berputar (motor)', en: 'Rotating electrical machines' },
    summary: {
      id: 'Standar motor: -1 pengenal dan unjuk kerja, -5 kode IP, -6 metode pendinginan (IC), -7 konstruksi pemasangan (IM), -30-1 kelas efisiensi IE1 sampai IE5. Yang penting untuk perancangan sirkit: arus start motor jauh di atas arus pengenalnya, sehingga proteksi hubung pendek dan proteksi beban lebih dipilih terpisah — satu pengaman yang memenuhi keduanya sekaligus biasanya salah satunya kompromi.',
      en: 'The motor standard: -1 rating and performance, -5 IP codes, -6 cooling methods (IC), -7 mounting arrangements (IM), -30-1 efficiency classes IE1 to IE5. What matters for circuit design: a motor’s starting current is far above its rated current, so short-circuit and overload protection are selected separately — one device sized for both is usually a compromise on one of them.',
    },
    keywords: [
      '60034', 'motor', 'ie3', 'ie4', 'efisiensi motor', 'arus start', 'starting current',
      'dol', 'star delta', 'sirkit motor', 'overload relay',
    ],
  },
  {
    id: 'nfpa-70',
    standard: 'NFPA 70 (National Electrical Code)',
    title: { id: 'Kode kelistrikan Amerika Serikat', en: 'US National Electrical Code' },
    summary: {
      id: 'Kode instalasi listrik Amerika Serikat, disusun per Article, bukan per bagian seperti IEC. Tidak berlaku di Indonesia — PUIL yang berlaku, dan PUIL diturunkan dari IEC 60364, bukan dari NEC. Relevan hanya bila kontrak, pemilik proyek atau EPC menetapkannya. Perbedaannya bukan kosmetik: satuan, penamaan penghantar, metode perhitungan KHA, dan filosofi proteksinya berbeda, sehingga mencampur keduanya dalam satu perhitungan menghasilkan angka yang tidak bisa dipertanggungjawabkan ke standar mana pun.',
      en: 'The United States electrical installation code, organised by Article rather than by part as IEC is. It does not apply in Indonesia — PUIL does, and PUIL derives from IEC 60364, not from the NEC. It is relevant only where a contract, owner or EPC specifies it. The differences are not cosmetic: units, conductor designations, capacity calculation methods and protection philosophy all differ, so mixing the two in one calculation produces a figure that answers to neither standard.',
    },
    keywords: ['nfpa 70', 'nec', 'national electrical code', 'kode amerika', 'awg'],
  },
  {
    id: 'slo-indonesia',
    standard: 'UU No. 30/2009 & aturan turunannya (SLO)',
    title: {
      id: 'Sertifikat Laik Operasi instalasi tenaga listrik',
      en: 'Indonesian operating-worthiness certificate for electrical installations',
    },
    summary: {
      id: 'Instalasi tenaga listrik di Indonesia wajib memiliki Sertifikat Laik Operasi sebelum dioperasikan, diterbitkan oleh lembaga inspeksi teknik yang terakreditasi setelah pemeriksaan dan pengujian. Dasarnya UU Ketenagalistrikan No. 30 Tahun 2009 beserta peraturan menteri turunannya — nomor dan tahun peraturan menteri yang berlaku berubah dari waktu ke waktu, jadi pastikan versi terbarunya sebelum dikutip di dokumen proyek. Ini kewajiban regulasi, terpisah dari kepatuhan teknis terhadap PUIL.',
      en: 'Electrical installations in Indonesia must hold an operating-worthiness certificate (SLO) before being energised, issued by an accredited technical inspection body after examination and testing. The basis is Electricity Law No. 30 of 2009 and the ministerial regulations under it — the applicable regulation number and year change over time, so confirm the current version before citing it in project documents. This is a regulatory obligation, separate from technical compliance with PUIL.',
    },
    keywords: [
      'slo', 'sertifikat laik operasi', 'laik operasi', 'lembaga inspeksi',
      'perizinan', 'regulasi', 'legalitas',
    ],
  },

  {
    id: 'puil-penampang-minimum',
    standard: 'PUIL 2011',
    title: { id: 'Luas penampang minimum penghantar', en: 'Minimum conductor cross-section' },
    summary: {
      id: 'PUIL 2011 menetapkan penampang minimum menurut fungsi sirkit dan jenis penghantar — bukan satu angka untuk semua. Praktik yang lazim di instalasi bangunan: 1,5 mm² untuk sirkit penerangan dan 2,5 mm² untuk sirkit kotak kontak, tetapi angka yang mengikat adalah hasil perhitungan KHA, susut tegangan dan proteksi hubung pendek.',
      en: 'PUIL 2011 sets minimum cross-sections by circuit function and conductor type — not one number for everything. Common building practice is 1.5 mm² for lighting circuits and 2.5 mm² for socket circuits, but the binding figure is what the capacity, voltage-drop and short-circuit calculations give.',
    },
    values: [
      { label: 'Sirkit penerangan (praktik)', value: '1,5 mm²' },
      { label: 'Sirkit kotak kontak (praktik)', value: '2,5 mm²' },
    ],
    keywords: [
      'penampang', 'cross section', 'mm2', 'mm²', 'ukuran', 'size', '1.5', '2.5',
      'susut tegangan', 'voltage drop', 'drop tegangan',
    ],
  },
];

/**
 * Lowercases, drops punctuation, and pads with spaces so a keyword can be
 * matched at word boundaries rather than as a substring.
 *
 * The padding is what makes ` pe ` mean the conductor and not the middle of
 * "petir", "speaker", "pemasangan" and "penerangan". Matching keywords as bare
 * substrings sent every lightning and public-address question to the entry on
 * conductor colours, and the shorter the keyword the wider the damage.
 *
 * `.` and `²` survive so "1.5" and "mm²" stay searchable.
 */
function normalize(text: string): string {
  return ` ${text.toLowerCase().replace(/[^\p{L}\p{N}.²]+/gu, ' ').trim()} `;
}

/**
 * Weight of one keyword hit.
 *
 * A phrase is worth most: "kotak kontak" can only be about one thing. Once
 * matching is bounded by word edges, length stops being a proxy for
 * specificity — "lux", "rcd", "poe" and "slo" are as decisive as any long word,
 * and scoring them below the bar dropped perfectly good questions.
 *
 * Two letters is where it stops being true. "ip" is the IP code here and an IP
 * address in the next sentence, and "am" is a fuse category and a word. Those
 * are worth a point on their own and need something else alongside.
 */
function weigh(keyword: string): number {
  if (keyword.includes(' ')) return 3;
  return keyword.length >= 3 ? 2 : 1;
}

/**
 * Below this, a match is not worth putting in front of the model.
 *
 * One short coincidental word is not a topic. Returning nothing is the better
 * answer there: the model is then told it has no checked notes and answers with
 * care, instead of being handed a reference that has nothing to do with the
 * question and treating it as the checked material.
 */
const MIN_SCORE = 2;

/**
 * The entries worth putting in front of the model for this question.
 *
 * Scored rather than filtered: a question mentioning both "kotak kontak" and
 * "RCD" should bring both entries, most relevant first, and a question about
 * neither should bring nothing rather than a default handful.
 */
export function findReferences(question: string, limit = 4): StandardReference[] {
  const haystack = normalize(question);
  if (haystack.trim() === '') return [];

  const scored = STANDARD_REFERENCES.map((entry) => {
    let score = 0;
    for (const keyword of entry.keywords) {
      if (haystack.includes(` ${keyword} `)) score += weigh(keyword);
    }
    return { entry, score };
  }).filter((row) => row.score >= MIN_SCORE);

  scored.sort((a, b) => b.score - a.score);
  return scored.slice(0, limit).map((row) => row.entry);
}

/** Renders the matched entries as the grounding block sent to Claude. */
export function referenceBlock(entries: StandardReference[]): string {
  if (entries.length === 0) return '';
  return entries
    .map((entry) => {
      const head = entry.clause ? `${entry.standard} — ${entry.clause}` : entry.standard;
      const values = entry.values
        ?.map((row) => `    ${row.label}: ${row.value}`)
        .join('\n');
      return [
        `- ${head}`,
        `  ${entry.title.id} / ${entry.title.en}`,
        `  ID: ${entry.summary.id}`,
        `  EN: ${entry.summary.en}`,
        values ? `  Figures:\n${values}` : '',
      ]
        .filter(Boolean)
        .join('\n');
    })
    .join('\n\n');
}
