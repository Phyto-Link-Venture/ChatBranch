/**
 * Release notes — what changed in each ChatBranch version, newest first, shown
 * on /whats-new. The versions must match the server's list in
 * backend-csharp/src/ChatBranch.Domain/Releases.cs exactly (UpdatesTests checks).
 *
 * Versioning: MAJOR.MINOR.PATCH. A patch only fixes things and reaches everyone;
 * a minor adds features (a company's admin may hold them); a major is only ever
 * taken when the admin chooses. When a minor's new feature should wait for the
 * admin's approval, give its change a `feature` key and wrap the UI in
 * useFeature(key) (lib/updates.ts).
 *
 * Write for customers: what they can do now, in plain words. Lead with new
 * features. Bahasa Melayu and 简体中文 are written here too; Manglish shows the
 * English.
 */

export type ChangeKind = 'new' | 'improved' | 'fixed' | 'removed'

export interface Localized {
  en: string
  ms: string
  zh: string
}

export interface ReleaseChange {
  kind: ChangeKind
  text: Localized
  /** Set when the change is a feature a holding company only gets once approved. */
  feature?: string
}

export interface Release {
  version: string
  /** ISO date it shipped. */
  date: string
  title: Localized
  changes: ReleaseChange[]
}

export const RELEASES: Release[] = [
  {
    version: '1.4.0',
    date: '2026-11-17',
    title: { en: 'Versions and What’s new', ms: 'Versi dan Apa yang baharu', zh: '版本与新功能' },
    changes: [
      {
        kind: 'new',
        text: {
          en: 'This What’s new page: every version’s new features, improvements and fixes in one place.',
          ms: 'Halaman Apa yang baharu ini: ciri baharu, penambahbaikan dan pembetulan setiap versi di satu tempat.',
          zh: '“新功能”页面：每个版本的新功能、改进和修复，一目了然。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'Admins choose how their company updates: take new features automatically, or hold them until you approve. Fixes always arrive.',
          ms: 'Pentadbir memilih cara syarikat dikemas kini: terima ciri baharu secara automatik, atau tahan sehingga anda luluskan. Pembetulan sentiasa diterima.',
          zh: '管理员可选择公司的更新方式：自动获取新功能，或在您批准前暂缓。修复始终会自动更新。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'When a new major version arrives, your company can stay on the version it knows until you decide to move.',
          ms: 'Apabila versi utama baharu tiba, syarikat anda boleh kekal pada versi yang biasa digunakan sehingga anda memutuskan untuk beralih.',
          zh: '推出新的主要版本时，您的公司可以继续使用熟悉的版本，直到您决定升级。',
        },
      },
    ],
  },
  {
    version: '1.3.0',
    date: '2026-11-17',
    title: { en: 'Access, help and new looks', ms: 'Akses, bantuan dan rupa baharu', zh: '权限、帮助与新外观' },
    changes: [
      {
        kind: 'new',
        text: {
          en: 'Choose which channel accounts each person can use, so a teammate only sees the numbers and inboxes they work on.',
          ms: 'Pilih akaun saluran yang boleh digunakan oleh setiap orang, supaya rakan sepasukan hanya melihat nombor dan peti masuk yang mereka uruskan.',
          zh: '可为每位成员指定可用的渠道账号，让同事只看到自己负责的号码和收件箱。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'Four new themes in Settings → Appearance: Pixel Paper, Risograph, Lacquer and Clay, each in light and dark.',
          ms: 'Empat tema baharu dalam Tetapan → Penampilan: Kertas Piksel, Risograf, Lakuer dan Tanah Liat, setiap satu dalam mod cerah dan gelap.',
          zh: '“设置 → 外观”新增四款主题：像素纸、孔版印刷、漆器和黏土，均提供浅色与深色模式。',
        },
      },
      {
        kind: 'improved',
        text: {
          en: 'The Help Center is public and easy to find from search engines and AI assistants.',
          ms: 'Pusat Bantuan kini terbuka kepada umum dan mudah dicari melalui enjin carian dan pembantu AI.',
          zh: '帮助中心现已公开，可通过搜索引擎和 AI 助手轻松找到。',
        },
      },
    ],
  },
  {
    version: '1.2.0',
    date: '2026-11-10',
    title: { en: 'Groups and easier connections', ms: 'Kumpulan dan sambungan lebih mudah', zh: '群聊与更简单的连接' },
    changes: [
      {
        kind: 'new',
        text: {
          en: 'Group chats: WhatsApp, Telegram and LINE groups arrive in your inbox, with each sender’s name.',
          ms: 'Sembang kumpulan: kumpulan WhatsApp, Telegram dan LINE masuk ke peti masuk anda, lengkap dengan nama setiap pengirim.',
          zh: '群聊：WhatsApp、Telegram 和 LINE 群组消息进入收件箱，并显示每位发送者的名字。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'Managed connections: connect WhatsApp, Messenger, Instagram, Telegram and SMS by signing in, without any developer setup.',
          ms: 'Sambungan terurus: sambungkan WhatsApp, Messenger, Instagram, Telegram dan SMS dengan log masuk sahaja, tanpa persediaan pembangun.',
          zh: '托管连接：只需登录即可连接 WhatsApp、Messenger、Instagram、Telegram 和短信，无需开发者设置。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'Voice cloning: record a few samples and your AI calls customers in that voice.',
          ms: 'Klon suara: rakam beberapa sampel dan AI anda menelefon pelanggan dengan suara itu.',
          zh: '声音克隆：录制几段样本，AI 就能用这个声音给客户打电话。',
        },
      },
      {
        kind: 'improved',
        text: {
          en: 'Channels shows one card per connected account, with its inbox and settings together.',
          ms: 'Saluran memaparkan satu kad bagi setiap akaun yang disambungkan, dengan peti masuk dan tetapannya bersama.',
          zh: '“渠道”页面为每个已连接账号显示一张卡片，收件箱与设置集中在一起。',
        },
      },
    ],
  },
  {
    version: '1.1.0',
    date: '2026-10-30',
    title: { en: 'Marketplace chats and voice notes', ms: 'Sembang pasaran dan nota suara', zh: '电商聊天与语音消息' },
    changes: [
      {
        kind: 'new',
        text: {
          en: 'Shopee, Lazada and TikTok Shop buyer chats in the same inbox as your other conversations.',
          ms: 'Sembang pembeli Shopee, Lazada dan TikTok Shop dalam peti masuk yang sama dengan perbualan lain anda.',
          zh: 'Shopee、Lazada 和 TikTok Shop 买家聊天与其他对话汇集在同一个收件箱。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'WhatsApp: send voice notes, delete a message for everyone, and edit a message you sent.',
          ms: 'WhatsApp: hantar nota suara, padam mesej untuk semua orang, dan sunting mesej yang telah dihantar.',
          zh: 'WhatsApp：可发送语音消息、为所有人删除消息，以及编辑已发送的消息。',
        },
      },
    ],
  },
  {
    version: '1.0.0',
    date: '2026-10-09',
    title: { en: 'The new ChatBranch', ms: 'ChatBranch baharu', zh: '全新 ChatBranch' },
    changes: [
      {
        kind: 'new',
        text: {
          en: 'One inbox for WhatsApp, Messenger, Instagram, Telegram, email, website chat, LINE, Viber, SMS, WeChat, Zalo and more.',
          ms: 'Satu peti masuk untuk WhatsApp, Messenger, Instagram, Telegram, e-mel, sembang laman web, LINE, Viber, SMS, WeChat, Zalo dan banyak lagi.',
          zh: '一个收件箱汇集 WhatsApp、Messenger、Instagram、Telegram、电子邮件、网站聊天、LINE、Viber、短信、微信、Zalo 等渠道。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'AI replies with an on/off switch and a schedule. It hands the chat to a person when needed, and those chats wait in Needs human.',
          ms: 'Balasan AI dengan suis hidup/mati dan jadual. Ia menyerahkan sembang kepada manusia apabila perlu, dan sembang itu menunggu dalam Perlu manusia.',
          zh: 'AI 自动回复，可一键开关并设置时间表；需要时会转交给真人，这些对话会在“需人工处理”中等候。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'The AI learns from your thumbs up and down, and from the documents you upload.',
          ms: 'AI belajar daripada penilaian suka dan tidak suka anda, serta daripada dokumen yang anda muat naik.',
          zh: 'AI 会从您的点赞、点踩以及上传的文档中学习。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'Bookings: the AI checks your calendar and books appointments for customers.',
          ms: 'Tempahan: AI menyemak kalendar anda dan menempah janji temu untuk pelanggan.',
          zh: '预约：AI 会查看您的日历并为客户预约时间。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'Journeys: follow up with customers over days, with your own conditions or “Let AI decide”.',
          ms: 'Perjalanan: susuli pelanggan selama beberapa hari, dengan syarat anda sendiri atau “Biar AI tentukan”.',
          zh: '客户旅程：在数天内持续跟进客户，可设定条件或选择“让 AI 决定”。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'AI playground: chat with your AI as if you were a customer before it talks to real people.',
          ms: 'Ruang uji AI: bersembang dengan AI anda seolah-olah anda pelanggan sebelum ia bercakap dengan orang sebenar.',
          zh: 'AI 试验场：在 AI 面对真实客户前，先以客户身份与它对话。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'Ask ChatBranch: an assistant that can do anything the dashboard does, and remembers your conversations with it.',
          ms: 'Tanya ChatBranch: pembantu yang boleh melakukan apa sahaja yang papan pemuka boleh lakukan, dan mengingati perbualan anda dengannya.',
          zh: '问问 ChatBranch：可完成控制台所有操作的助手，并会记住您与它的对话。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'The dashboard speaks English, Bahasa Melayu and 简体中文.',
          ms: 'Papan pemuka tersedia dalam bahasa Inggeris, Bahasa Melayu dan 简体中文.',
          zh: '控制台支持英语、马来语和简体中文。',
        },
      },
      {
        kind: 'new',
        text: {
          en: 'Contacts: one person across all their channels, invite links, and importing your contact book.',
          ms: 'Kenalan: satu individu merentas semua saluran mereka, pautan jemputan, dan import buku kenalan anda.',
          zh: '联系人：同一位客户的所有渠道合而为一，支持邀请链接和导入通讯录。',
        },
      },
      {
        kind: 'improved',
        text: {
          en: 'Send files on every channel that allows them, and customers see “typing…” on Messenger, Instagram and Telegram.',
          ms: 'Hantar fail pada setiap saluran yang membenarkannya, dan pelanggan melihat “sedang menaip…” di Messenger, Instagram dan Telegram.',
          zh: '所有支持的渠道均可发送文件，客户在 Messenger、Instagram 和 Telegram 上会看到“正在输入…”。',
        },
      },
    ],
  },
]

/** The version that introduced a gated feature (see `feature` above), or null. */
export function featureVersion(key: string): string | null {
  for (const r of RELEASES) if (r.changes.some((c) => c.feature === key)) return r.version
  return null
}
