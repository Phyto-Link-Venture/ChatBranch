# Translation glossary — ChatBranch dashboard

One word per concept, everywhere. Translators (people or agents) use these
terms; a new recurring term gets a row here in the same change. Malay is
Malaysian standard (DBP) as used in Malaysian apps; Chinese is Simplified, with
full-width punctuation (，。：？！“”).

| English | Bahasa Melayu | 简体中文 |
|---|---|---|
| chat / chats (the inbox) | sembang | 对话 |
| conversation (one thread) | perbualan | 对话 |
| message | mesej | 消息 |
| contact | kenalan | 联系人 |
| customer | pelanggan | 客户 |
| entity | entiti | 实体 |
| outlet / branch | cawangan | 分店 |
| head office | ibu pejabat | 总部 |
| inbox (a connected channel account) | peti masuk | 收件箱 |
| channel | saluran | 渠道 |
| account / number (channel account) | akaun / nombor | 账号 / 号码 |
| template | templat | 模板 |
| quick reply (canned response) | balasan pantas | 快捷回复 |
| call script | skrip panggilan | 通话脚本 |
| journey (the flow builder; one automated sequence) | perjalanan | 客户旅程 |
| flow (internal: the engine, the API) | aliran | 流程 |
| trigger | pencetus | 触发条件 |
| step | langkah | 步骤 |
| automation (rule) | automasi (peraturan) | 自动化规则 |
| campaign | kempen | 群发活动 |
| AI skill | kemahiran AI | AI 技能 |
| knowledge base | pangkalan pengetahuan | 知识库 |
| lesson (AI learning) | pengajaran | 经验 |
| draft (AI) | draf | 草稿 |
| approve / reject | luluskan / tolak | 批准 / 拒绝 |
| hand over (to a person) | serahkan kepada manusia | 转人工 |
| needs a human | perlukan manusia | 需人工处理 |
| assign / assignee | tugaskan / penerima tugasan | 指派 / 负责人 |
| team | pasukan | 团队 |
| agent (human role) | ejen | 客服 |
| admin / superadmin | pentadbir / pentadbir super | 管理员 / 超级管理员 |
| role | peranan | 角色 |
| permission / capability | kebenaran | 权限 |
| open (status) | terbuka | 进行中 |
| pending (status) | menunggu | 待处理 |
| resolved | selesai | 已解决 |
| snoozed | ditunda | 已延后 |
| opted out / opt out | telah menarik diri / tarik diri | 已退订 / 退订 |
| quiet hours | waktu senyap | 免打扰时段 |
| booking / book | tempahan / tempah | 预约 / 预约 |
| slot (time) | slot | 时段 |
| tag | tag | 标签 |
| label (conversation label) | label | 标记 |
| custom field | medan tersuai | 自定义字段 |
| integration | integrasi | 集成 |
| connection | sambungan | 连接 |
| health (of a channel) | kesihatan | 健康状态 |
| sandbox | kotak pasir | 沙盒 |
| webhook | webhook | Webhook |
| API key | kunci API | API 密钥 |
| settings | tetapan | 设置 |
| analytics | analitik | 分析 |
| activity log | log aktiviti | 活动记录 |
| call / voice call | panggilan / panggilan suara | 通话 / 语音通话 |
| follow-up | susulan | 跟进 |
| unknown customer | pelanggan tidak dikenali | 未知客户 |
| invite link | pautan jemputan | 邀请链接 |
| import | import | 导入 |
| test lab | makmal ujian | 测试实验室 |
| AI playground | ruang uji AI | AI 试验场 |
| set-up (a playground configuration being compared) | tetapan uji | 方案 |
| persona (the AI's voice) | persona | 人设 |

Never translate: ChatBranch, WhatsApp, Telegram, Messenger, Instagram, LINE,
Viber, WeChat, TikTok, Douyin, Zalo, Google, Gmail, Shopify, Stripe, MCP, SMS,
LiveKit, Baileys, capability ids (`chats.view`), code, URLs.

## Manglish (`messages/manglish/`) — the dashboard's friendly Malaysian-English voice

Manglish is an OVERLAY on English (`Overlay<T>`): a file holds only the strings
it rewrites, and every key it leaves out reads in English. It is a tone, not a
new vocabulary — anyone who reads the English UI must still understand it at once.

**Rewrite** the sentences that talk to the person: help text under a field,
hints, notices and banners, empty states ("No chats yet…"), confirmations
("Delete this…?"), success / error messages, loading lines, tooltips that are
full sentences, the sign-in page, the page guides (`guide.tours.*`).

**Keep exactly as in English** (leave the key out):
- anything a person clicks or the manual quotes: button, menu, tab, column,
  field, option and page names, step names, status chips, units, short labels;
- words that reach a CUSTOMER or the AI: starter journey messages, default
  replies, templates, AI briefs / instructions / prompts, example customer text
  (`flows.starters.*`, `readySkills` templates, identity-ask wording…);
- product and brand names, codes, ids, URLs, `{{variables}}`.

**Voice:** warm, short, a bit cheeky but never rude or crude; plain words
over formal ones ("Got problem saving lah — try again?" not "An error has
occurred"). Particles — lah, ah, leh, liao, one, can — at most one per
sentence and not in every sentence; common Malaysian words people use at work
are fine (boleh, sikit, jom, settle, on the way); no Hokkien swear
words, no slang that needs explaining, no mocking the reader. Numbers, dates,
`{placeholders}` and `<tags>` stay exactly as in English; plural pairs
(`x_one` / `x_other`) are rewritten together. Check with
`node scripts/check-manglish.mjs <namespace>` (frontend).

| English | Manglish |
|---|---|
| Something went wrong. Try again. | Aiyo, something went wrong. Try again can? |
| Saved. | Saved liao. |
| Are you sure? | Confirm ah? |
| No results. | Nothing found leh. |
| Loading… | Loading, wait ah… |
| You don't have access to this page. | Sorry ah, this page not for your account. |
| Please choose one. | Choose one first lah. |
