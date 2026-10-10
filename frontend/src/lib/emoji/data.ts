/**
 * The composer's built-in emoji set: ~300 everyday emoji in 7 categories, each
 * with search words in English, Bahasa Melayu and 简体中文 (data, not UI copy —
 * the picker matches a query against all three, whatever the UI language).
 *
 * Row format: "emoji|english words|malay words|chinese words". Kept as plain
 * strings so the whole table is a few KB in the bundle (no emoji package).
 */

export type EmojiCategory = 'smileys' | 'people' | 'hearts' | 'nature' | 'food' | 'activities' | 'objects'

export interface EmojiEntry {
  char: string
  /** lower-cased search text in every language */
  words: string
  /** the English name (the button's tooltip fallback) */
  name: string
  category: EmojiCategory
}

const RAW: Record<EmojiCategory, string> = {
  smileys: `
😀|grinning smile happy|senyum gembira|笑 开心 微笑
😃|smiley happy big smile|senyum lebar gembira|大笑 开心
😄|smile laugh eyes|senyum ketawa|笑眯眯 开心
😁|grin beaming|sengih|咧嘴笑
😆|laughing squint|ketawa besar|大笑 哈哈
😅|sweat smile nervous|senyum berpeluh gugup|苦笑 汗
🤣|rofl rolling laughing|ketawa berguling|笑翻 哈哈哈
😂|joy tears laughing lol|ketawa menangis|笑哭 哈哈
🙂|slightly smiling|senyum sikit|微笑
🙃|upside down silly|terbalik|倒脸
😉|wink|kenyit mata|眨眼
😊|blush smiling happy|senyum malu|害羞 微笑
😇|angel halo innocent|malaikat|天使
🥰|love hearts adore|sayang cinta|喜爱 爱心
😍|heart eyes love|mata cinta|花痴 爱
🤩|star struck wow|kagum bintang|崇拜 哇
😘|kiss blowing|cium|飞吻 亲
😗|kissing|cium|亲亲
😚|kissing closed eyes|cium|亲亲
😋|yum delicious tasty|sedap|好吃 美味
😛|tongue|jelir lidah|吐舌
😜|wink tongue crazy|jelir kenyit|调皮
🤪|zany crazy|gila|疯狂 搞怪
😝|squint tongue|jelir|吐舌头
🤑|money mouth rich|duit kaya|发财 钱
🤗|hug hugging|peluk|拥抱
🤭|hand over mouth giggle|tutup mulut|偷笑 捂嘴
🤫|shush quiet secret|senyap rahsia|嘘 安静
🤔|thinking hmm|fikir|思考 想
🤐|zipper mouth|zip mulut diam|闭嘴
🤨|raised eyebrow doubt|kening ragu|怀疑
😐|neutral|neutral biasa|面无表情
😑|expressionless|tiada ekspresi|无语
😶|no mouth speechless|tak bercakap|沉默
😏|smirk|senyum sinis|得意 坏笑
😒|unamused|tak suka bosan|不高兴
🙄|eye roll|juling mata|翻白眼
😬|grimace awkward|janggal|尴尬
😮‍💨|exhale relief sigh|lega mengeluh|松口气
🤥|lying pinocchio|tipu|说谎
😌|relieved calm|lega tenang|释然
😔|pensive sad|sedih termenung|沉思 难过
😪|sleepy|mengantuk|困
🤤|drooling|air liur|流口水
😴|sleeping zzz|tidur|睡觉
😷|mask sick|topeng sakit|口罩 生病
🤒|thermometer fever sick|demam|发烧
🤕|bandage hurt|cedera|受伤
🤢|nauseated sick|loya|恶心
🤮|vomit|muntah|呕吐
🥵|hot sweating|panas|热
🥶|cold freezing|sejuk|冷
🥴|woozy dizzy|pening|晕
😵|dizzy|pening|晕倒
🤯|mind blown exploding|terkejut gila|震惊 爆炸
🤠|cowboy|koboi|牛仔
🥳|party celebrate|parti raikan|庆祝 派对
😎|cool sunglasses|cool cermin mata hitam|酷 墨镜
🤓|nerd geek|nerd|书呆子
🧐|monocle inspect|teliti|研究
😕|confused|keliru|困惑
😟|worried|risau|担心
🙁|frowning|masam|皱眉
😮|open mouth wow surprised|terkejut|惊讶 哇
😯|hushed surprised|terkejut|惊讶
😲|astonished shocked|terperanjat|震惊
😳|flushed embarrassed|malu merah|脸红
🥺|pleading puppy eyes please|merayu|可怜 求求
😦|frowning open mouth|terkejut|吃惊
😧|anguished|derita|痛苦
😨|fearful scared|takut|害怕
😰|anxious sweat|cemas|焦虑
😥|sad relieved|sedih|失望
😢|cry sad tear|menangis sedih|哭 难过
😭|sob crying loudly|meraung menangis|大哭
😱|scream fear|menjerit takut|尖叫 恐惧
😖|confounded|kecewa|困扰
😣|persevering|bertahan|忍耐
😞|disappointed|kecewa|失望
😓|downcast sweat|kecewa|沮丧
😩|weary tired|penat|疲惫
😫|tired|letih|累
🥱|yawning bored|menguap|打哈欠
😤|triumph huff|marah dengus|生气 哼
😡|angry pouting rage|marah|愤怒
😠|angry|marah|生气
🤬|cursing swearing|maki|骂人
😈|devil smiling|syaitan|恶魔
💀|skull dead|tengkorak mati|骷髅
💩|poop|najis|便便
🤡|clown|badut|小丑
👻|ghost|hantu|鬼
👽|alien|makhluk asing|外星人
🤖|robot bot|robot|机器人
😺|cat smiling|kucing senyum|猫 笑
😻|cat heart eyes|kucing cinta|猫 爱
🙈|see no evil monkey|monyet tutup mata|捂眼 猴子
🙉|hear no evil monkey|monyet tutup telinga|捂耳 猴子
🙊|speak no evil monkey|monyet tutup mulut|捂嘴 猴子`,
  people: `
👍|thumbs up like yes ok good|bagus suka ya setuju|赞 好 同意 点赞
👎|thumbs down dislike no|tak suka tidak|踩 不好
👌|ok hand perfect|ok baik|好的 OK
🤌|pinched fingers|jari cubit|捏手指
🤏|pinching small|sikit|一点点
✌️|victory peace|aman kemenangan|胜利 耶
🤞|fingers crossed luck|semoga berjaya|祈祷 好运
🤟|love you gesture|sayang|爱你
🤘|rock on horns|rock|摇滚
🤙|call me shaka|telefon saya|打电话
👈|point left|tunjuk kiri|左
👉|point right|tunjuk kanan|右
👆|point up|tunjuk atas|上
👇|point down|tunjuk bawah|下
☝️|index up one|satu|一
✋|raised hand stop|tangan angkat berhenti|举手 停
🤚|raised back hand|tangan|手
🖐️|hand fingers splayed|tangan|手掌
🖖|vulcan salute|salam|瓦肯
👋|wave hello bye hi|lambai hai selamat tinggal|挥手 你好 再见
🤝|handshake deal agree|jabat tangan setuju|握手 合作
👏|clap applause bravo|tepuk tangan|鼓掌
🙌|raising hands hooray|sorak|欢呼
👐|open hands|tangan terbuka|张开手
🤲|palms up prayer|tadah tangan doa|祈求
🙏|pray please thanks thank you|terima kasih tolong doa|谢谢 拜托 祈祷
✍️|writing|menulis|写
💪|muscle strong flex|kuat|加油 强壮
🫶|heart hands love|tangan hati|比心
🫡|salute|tabik|敬礼
👀|eyes look see|mata lihat|看
👁️|eye|mata|眼睛
👂|ear listen|telinga dengar|耳朵
👃|nose smell|hidung|鼻子
🧠|brain smart|otak pandai|大脑 聪明
👶|baby|bayi|婴儿
🧒|child|kanak-kanak|孩子
👦|boy|budak lelaki|男孩
👧|girl|budak perempuan|女孩
🧑|person|orang|人
👨|man|lelaki|男人
👩|woman|perempuan|女人
🧓|older person|warga emas|老人
👴|old man grandpa|atuk|爷爷
👵|old woman grandma|nenek|奶奶
🙋|raising hand question|angkat tangan|举手 提问
🙆|ok gesture|ok|好
🙅|no gesture|tidak|不行
🤷|shrug dont know|angkat bahu tak tahu|耸肩 不知道
🤦|facepalm|tepuk dahi|捂脸 无语
💁|tipping hand info|maklumat|信息
🙇|bow sorry|tunduk maaf|鞠躬 对不起
👨‍💻|technologist developer|juruteknologi|程序员
🧑‍🍳|cook chef|tukang masak|厨师
🧑‍⚕️|health worker doctor|doktor|医生
👮|police officer|polis|警察
🕵️|detective|detektif|侦探
💃|dancing woman dance|menari|跳舞
🕺|dancing man|menari|跳舞
🏃|running run|berlari|跑步
🚶|walking walk|berjalan|走路
👪|family|keluarga|家庭`,
  hearts: `
❤️|red heart love|hati merah cinta sayang|红心 爱
🧡|orange heart|hati oren|橙心
💛|yellow heart|hati kuning|黄心
💚|green heart|hati hijau|绿心
💙|blue heart|hati biru|蓝心
💜|purple heart|hati ungu|紫心
🖤|black heart|hati hitam|黑心
🤍|white heart|hati putih|白心
🤎|brown heart|hati coklat|棕心
💔|broken heart|patah hati|心碎
❤️‍🔥|heart on fire|hati berapi|燃烧的心
💕|two hearts|dua hati|两颗心
💞|revolving hearts|hati berputar|旋转的心
💓|beating heart|hati berdegup|心跳
💗|growing heart|hati membesar|心动
💖|sparkling heart|hati berkilau|闪亮的心
💘|heart arrow cupid|hati panah|丘比特
💝|heart ribbon gift|hati hadiah|爱心礼物
💯|hundred perfect score|seratus sempurna|一百 满分
💢|anger symbol|marah|生气
💥|collision boom|letupan|爆炸
💫|dizzy star|bintang|晕
💦|sweat droplets|titisan|汗滴
💨|dash fast|laju|快
💬|speech bubble chat|bual|对话
💭|thought bubble|fikiran|想法
💤|zzz sleep|tidur|睡觉
✅|check done yes|siap betul|对 完成
☑️|check box|kotak tanda|勾选
✔️|check mark|tanda betul|对勾
❌|cross no wrong|salah tidak|错 不
❎|cross mark button|pangkah|叉
⭕|circle|bulatan|圆圈
🚫|prohibited no|dilarang|禁止
⛔|no entry|dilarang masuk|禁止进入
⚠️|warning|amaran|警告
❗|exclamation important|penting|感叹号 重要
❓|question|soalan|问号
‼️|double exclamation|seru|双感叹号
⁉️|exclamation question|seru soal|疑问
🔥|fire hot lit|api panas|火 热
✨|sparkles new|berkilau|闪亮
⭐|star|bintang|星星
🌟|glowing star|bintang bersinar|闪亮的星
⚡|lightning fast|kilat|闪电
🎉|party popper tada celebrate|raikan tahniah|庆祝 恭喜
🎊|confetti ball|konfeti|彩带
🆗|ok button|ok|OK
🆕|new button|baru|新
🆓|free button|percuma|免费
🔝|top|atas|顶
🔴|red circle|bulat merah|红圈
🟢|green circle|bulat hijau|绿圈
🔵|blue circle|bulat biru|蓝圈
➕|plus add|tambah|加
➖|minus|tolak|减
➡️|right arrow|anak panah kanan|右箭头
⬅️|left arrow|anak panah kiri|左箭头
⬆️|up arrow|anak panah atas|上箭头
⬇️|down arrow|anak panah bawah|下箭头
🔁|repeat|ulang|重复
♻️|recycle|kitar semula|回收
🕐|one oclock time|pukul satu masa|一点 时间`,
  nature: `
🐶|dog puppy|anjing|狗
🐱|cat kitten|kucing|猫
🐭|mouse|tikus|老鼠
🐰|rabbit bunny|arnab|兔子
🦊|fox|musang|狐狸
🐻|bear|beruang|熊
🐼|panda|panda|熊猫
🐨|koala|koala|考拉
🐯|tiger|harimau|老虎
🦁|lion|singa|狮子
🐮|cow|lembu|牛
🐷|pig|babi|猪
🐸|frog|katak|青蛙
🐵|monkey|monyet|猴子
🐔|chicken|ayam|鸡
🐧|penguin|penguin|企鹅
🐦|bird|burung|鸟
🦆|duck|itik|鸭
🦉|owl|burung hantu|猫头鹰
🐴|horse|kuda|马
🦄|unicorn|unikorn|独角兽
🐝|bee|lebah|蜜蜂
🦋|butterfly|rama-rama|蝴蝶
🐢|turtle|penyu|乌龟
🐍|snake|ular|蛇
🐙|octopus|sotong kurita|章鱼
🐟|fish|ikan|鱼
🐬|dolphin|lumba-lumba|海豚
🐳|whale|ikan paus|鲸鱼
🐘|elephant|gajah|大象
🐪|camel|unta|骆驼
🌸|cherry blossom flower|bunga sakura|樱花
🌹|rose|mawar|玫瑰
🌺|hibiscus|bunga raya|扶桑花
🌻|sunflower|bunga matahari|向日葵
🌷|tulip|tulip|郁金香
💐|bouquet flowers|jambangan bunga|花束
🌱|seedling plant grow|anak pokok|发芽
🌴|palm tree|pokok kelapa|棕榈树
🌳|tree|pokok|树
🍀|four leaf clover luck|semanggi tuah|四叶草 幸运
🍁|maple leaf|daun|枫叶
☀️|sun sunny|matahari cerah|太阳 晴
🌤️|sun cloud|cerah berawan|晴转多云
☁️|cloud|awan|云
🌧️|rain|hujan|雨
⛈️|storm thunder|ribut petir|雷雨
🌈|rainbow|pelangi|彩虹
❄️|snowflake cold|salji|雪花
🌙|moon night|bulan malam|月亮
🌍|earth world globe|bumi dunia|地球 世界
🌊|wave sea|ombak laut|海浪`,
  food: `
🍎|apple|epal|苹果
🍊|orange|oren|橙子
🍋|lemon|lemon|柠檬
🍌|banana|pisang|香蕉
🍉|watermelon|tembikai|西瓜
🍇|grapes|anggur|葡萄
🍓|strawberry|strawberi|草莓
🥭|mango|mangga|芒果
🍍|pineapple|nanas|菠萝
🥥|coconut|kelapa|椰子
🥑|avocado|avokado|牛油果
🌶️|chili hot pepper|cili pedas|辣椒
🌽|corn|jagung|玉米
🥕|carrot|lobak merah|胡萝卜
🍞|bread|roti|面包
🥐|croissant|croissant|牛角包
🧀|cheese|keju|奶酪
🍳|cooking egg|telur goreng|煎蛋
🍔|burger|burger|汉堡
🍟|fries|kentang goreng|薯条
🍕|pizza|piza|披萨
🌭|hot dog|hot dog|热狗
🌮|taco|taco|塔可
🍜|noodles ramen|mi|面条 拉面
🍝|spaghetti pasta|spageti|意面
🍛|curry rice|nasi kari|咖喱饭
🍚|rice|nasi|米饭
🍣|sushi|sushi|寿司
🥟|dumpling|ladu|饺子
🍤|shrimp prawn|udang|虾
🍗|chicken leg|paha ayam|鸡腿
🍖|meat|daging|肉
🥗|salad|salad|沙拉
🍲|stew pot|periuk|火锅 炖
🍰|cake|kek|蛋糕
🎂|birthday cake|kek hari jadi|生日蛋糕
🧁|cupcake|kek cawan|纸杯蛋糕
🍩|donut|donat|甜甜圈
🍪|cookie|biskut|饼干
🍫|chocolate|coklat|巧克力
🍬|candy|gula-gula|糖果
🍦|ice cream|aiskrim|冰淇淋
☕|coffee hot drink|kopi|咖啡
🍵|tea|teh|茶
🧋|bubble tea boba|teh boba|奶茶 珍珠
🥤|soft drink cup|minuman|饮料
🧃|juice box|jus|果汁
🍺|beer|bir|啤酒
🥂|cheers toast|bersulang|干杯
🍷|wine|wain|葡萄酒
🥢|chopsticks|penyepit|筷子
🍽️|plate cutlery dinner|pinggan makan|餐具 吃饭`,
  activities: `
⚽|soccer football|bola sepak|足球
🏀|basketball|bola keranjang|篮球
🏈|american football|bola|橄榄球
⚾|baseball|besbol|棒球
🎾|tennis|tenis|网球
🏸|badminton|badminton|羽毛球
🏓|ping pong table tennis|ping pong|乒乓球
🏊|swimming|berenang|游泳
🚴|cycling bike|berbasikal|骑车
🏋️|weightlifting gym|angkat berat|举重 健身
🧘|yoga|yoga|瑜伽
🏆|trophy winner|piala juara|奖杯 冠军
🥇|gold medal first|pingat emas|金牌 第一
🎯|target bullseye goal|sasaran|目标
🎮|video game|permainan video|游戏
🎲|dice game|dadu|骰子
🎨|art palette|seni|艺术
🎬|movie film|filem|电影
🎤|microphone sing karaoke|mikrofon nyanyi|唱歌 麦克风
🎧|headphones music|fon kepala|耳机
🎵|music note|muzik|音乐
🎸|guitar|gitar|吉他
🎁|gift present|hadiah|礼物
🎈|balloon|belon|气球
🎄|christmas tree|pokok krismas|圣诞树
🧧|red envelope angpao|angpau sampul merah|红包
🏮|lantern|tanglung|灯笼
🌙|crescent ramadan raya|bulan sabit raya|弯月
✈️|airplane flight travel|kapal terbang|飞机 旅行
🚗|car|kereta|汽车
🚕|taxi|teksi|出租车
🚌|bus|bas|巴士
🚆|train|kereta api|火车
🚲|bicycle|basikal|自行车
🛵|scooter motorbike delivery|skuter motosikal|摩托车
🚚|delivery truck|lori penghantaran|货车 送货
⛽|fuel petrol|minyak|加油站
🚦|traffic light|lampu isyarat|红绿灯
🏠|house home|rumah|房子 家
🏢|office building|pejabat|办公楼
🏥|hospital|hospital|医院
🏫|school|sekolah|学校
🏪|shop store|kedai|商店
🏖️|beach holiday|pantai cuti|海滩 度假
⛰️|mountain|gunung|山
🗺️|map|peta|地图
📍|pin location|lokasi|位置 定位`,
  objects: `
📱|phone mobile|telefon|手机
💻|laptop computer|komputer riba|电脑
⌨️|keyboard|papan kekunci|键盘
🖨️|printer|pencetak|打印机
📷|camera photo|kamera|相机
📞|telephone call|telefon panggil|电话
☎️|phone|telefon|电话
📧|email|e-mel|邮件
✉️|envelope letter|sampul surat|信封
📦|package parcel box|bungkusan|包裹
📮|postbox|peti surat|邮箱
📝|memo note write|nota|笔记
📄|document page|dokumen|文件
📅|calendar date|kalendar|日历
📆|calendar|kalendar|日历
📌|pushpin|pin|图钉
📎|paperclip attach|klip kertas|回形针 附件
✂️|scissors|gunting|剪刀
🔒|locked secure|berkunci|锁
🔓|unlocked|tidak berkunci|开锁
🔑|key|kunci|钥匙
🔔|bell notification|loceng|铃铛 通知
🔕|bell off mute|senyap|静音
📢|loudspeaker announce|pembesar suara|喇叭 公告
💡|idea light bulb|idea mentol|想法 灯泡
🔍|search magnifier|cari|搜索
⏰|alarm clock|jam loceng|闹钟
⌛|hourglass wait|tunggu|沙漏 等待
⏳|hourglass flowing|menunggu|等待
💰|money bag|duit|钱袋
💵|dollar cash|wang tunai|现金
💳|credit card payment|kad kredit bayar|信用卡 付款
🧾|receipt invoice|resit|收据 发票
🛒|shopping cart|troli beli|购物车
🛍️|shopping bags|beg beli-belah|购物袋
🏷️|label tag price|label harga|标签 价格
📊|chart bar stats|carta|图表
📈|chart up growth|carta naik|增长
📉|chart down|carta turun|下降
🗂️|folders|fail|文件夹
🗑️|trash bin|tong sampah|垃圾桶
🔧|wrench fix|sepana baiki|扳手 修理
🔨|hammer|tukul|锤子
⚙️|gear settings|tetapan|设置 齿轮
🧰|toolbox|kotak alat|工具箱
💊|pill medicine|ubat|药
🩺|stethoscope doctor|stetoskop|听诊器
🚪|door|pintu|门
🛏️|bed|katil|床
🎓|graduation|graduasi|毕业
📚|books study|buku|书
✏️|pencil|pensel|铅笔
🖊️|pen|pen|笔
🔗|link|pautan|链接
🧲|magnet|magnet|磁铁
🪙|coin|syiling|硬币`,
}

function parse(): EmojiEntry[] {
  const out: EmojiEntry[] = []
  const seen = new Set<string>()
  for (const [category, block] of Object.entries(RAW) as [EmojiCategory, string][]) {
    for (const line of block.split('\n')) {
      const [char, en = '', ms = '', zh = ''] = line.trim().split('|')
      if (!char || seen.has(char)) continue
      seen.add(char)
      out.push({ char, name: en.split(' ').slice(0, 3).join(' '), words: `${en} ${ms} ${zh}`.toLowerCase(), category })
    }
  }
  return out
}

export const EMOJI: EmojiEntry[] = parse()

export const EMOJI_CATEGORIES: EmojiCategory[] = ['smileys', 'people', 'hearts', 'nature', 'food', 'activities', 'objects']

/** The category tab icons (an emoji of each). */
export const CATEGORY_ICON: Record<EmojiCategory, string> = {
  smileys: '😀',
  people: '👋',
  hearts: '❤️',
  nature: '🌸',
  food: '🍜',
  activities: '⚽',
  objects: '💡',
}

/** Every query word must appear in the entry's words (any language). */
export function searchEmoji(query: string, limit = 120): EmojiEntry[] {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean)
  if (terms.length === 0) return []
  return EMOJI.filter((e) => terms.every((t) => e.words.includes(t) || e.char === t)).slice(0, limit)
}

/** The quick-reaction bar's six (WhatsApp's default set, 🙏 for 👏). */
export const QUICK_REACTIONS = ['👍', '❤️', '😂', '😮', '😢', '🙏']

const RECENT_KEY = 'emoji-recent'
const RECENT_MAX = 24

/** Recently used emoji, newest first (per browser; empty when storage is unavailable). */
export function recentEmoji(): string[] {
  try {
    const raw = localStorage.getItem(RECENT_KEY)
    const list = raw ? (JSON.parse(raw) as unknown) : []
    return Array.isArray(list) ? list.filter((x): x is string => typeof x === 'string').slice(0, RECENT_MAX) : []
  } catch {
    return []
  }
}

export function rememberEmoji(char: string): void {
  try {
    const next = [char, ...recentEmoji().filter((c) => c !== char)].slice(0, RECENT_MAX)
    localStorage.setItem(RECENT_KEY, JSON.stringify(next))
  } catch {
    /* storage unavailable — recents just don't persist */
  }
}
