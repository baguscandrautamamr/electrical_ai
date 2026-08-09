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
    keywords: ['puil', 'sni 0225', '0225', 'standar', 'standard', 'instalasi', 'installation'],
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
    keywords: [
      'rcd', 'gpas', 'elcb', 'rcbo', 'arus sisa', 'residual', 'bocor', 'leakage',
      'kamar mandi', 'bathroom', 'basah', 'wet', 'proteksi', 'protection', '30 ma',
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
    keywords: [
      'warna', 'colour', 'color', 'kabel', 'cable', 'penghantar', 'conductor',
      'inti', 'core', 'netral', 'neutral', 'fasa', 'phase', 'pe', 'arde',
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
    keywords: [
      'kebakaran', 'fire', 'alarm', 'detektor', 'detector', 'smoke', 'asap', 'heat',
      '3985', 'nfpa', 'sprinkler', 'titik panggil',
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

/** Strips punctuation so "PUIL?" matches "puil". */
function normalize(text: string): string {
  return text.toLowerCase().replace(/[^\p{L}\p{N}\s.²]/gu, ' ');
}

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
      if (!haystack.includes(keyword)) continue;
      // A multi-word hit ("kotak kontak") is a much stronger signal than a
      // single common word ("kabel"), so weight by length.
      score += keyword.includes(' ') ? 3 : keyword.length >= 5 ? 2 : 1;
    }
    return { entry, score };
  }).filter((row) => row.score > 0);

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
