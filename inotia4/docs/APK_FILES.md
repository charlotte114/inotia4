# 艾诺迪亚4 安装包文件清单（逐项用途）

对象：`艾诺迪亚4_1.2.9_cat_final.apk`（49,578,397 B，2081 个条目，解压后 57,794,620 B）
基线：`艾诺迪亚4_1.2.9_cat.apk`（同 2081 条目，签名 `CN=Android`，未改动）

```
包名        com.com2us.inotia4.cat
版本        1.2.9 (versionCode 29)
SDK         minSdk 15 / targetSdk 27
ABI         仅 armeabi-v7a
Application android.support.multidex.MultiDexApplication
签名        v1 + v2 + v3，证书 CN=InotiaMod, O=Mod, C=CN（RSA 2048）
```

---

## 0. 体积构成

| 分类 | 条目 | 解压后 | 占比 |
|---|---:|---:|---:|
| `assets/common/game_res/` 游戏资源 | 549 | 21,526,105 B | 37.3% |
| `assets/sound/` 音频 | 3 | 18,437,034 B | 31.9% |
| `*.dex` 代码 | 3 | 10,293,548 B | 17.8% |
| `lib/` 原生库 | 4 | 2,922,104 B | 5.1% |
| `res/` Android 资源 | 1388 | 2,682,322 B | 4.6% |
| `resources.arsc` | 1 | 929,748 B | 1.6% |
| `META-INF/` 签名 | 16 | 424,869 B | 0.7% |
| `assets/common/ActiveUser*` 协议/弹窗 | 65 | 391,839 B | 0.7% |
| `assets/common/mercury/` 推广弹窗 | 40 | 104,054 B | 0.2% |
| `assets/` 其它（含 4 个 WBSA 库） | 5 | 54,792 B | 0.1% |
| `AndroidManifest.xml` | 1 | 20,376 B | — |
| 根目录其它 | 4 | 4,513 B | — |
| `assets/common/offerwall/` | 2 | 3,316 B | — |

压缩方式：1705 个条目 **STORED（不压缩）**，43,323,511 B；其余 376 个 DEFLATED。
不压缩是刻意的——引擎按文件偏移直读 `assets`（`MMappedFile`/`ResourceManager`），zip 解压会破坏 mmap。

---

## 1. 根目录（8 个）

| 文件 | 大小 | 用途 |
|---|---:|---|
| `AndroidManifest.xml` | 20,376 | 二进制 AXML。1 个自定义 permission、**16 个 uses-permission、15 个 activity、11 个 service、10 个 receiver、1 个 provider、4 个 meta-data**，详见第 2 节 |
| `classes.dex` | 8,994,896 | 主 dex，9782 个类。**已改**（原 8,994,848，+48 B） |
| `classes2.dex` | 1,276,592 | 1736 个类：`com.google`(1209) + `org.apache`(388，httpclient) + `com.tencent`(138，QQ 互联 SDK) + `org.fmod`(1) |
| `classes3.dex` | 22,060 | 第三方注入的 10 个类：`com.noname.inotia4.Old_Fix` + `org.lsposed.hiddenapibypass.HiddenApiBypass`（含 8 个 Helper 内部类） |
| `resources.arsc` | 929,748 | 资源索引表，STORED |
| `protobuf.meta` | 4,168 | Google Tink 加密库的 protobuf 描述符（`google.cloud.crypto.tink.*` → `CommonProto$*` / `TinkProto$*`），随 GMS 一起打进来，本游戏未直接使用 |
| `build-data.properties` | 159 | GMS 构建残留：`build.target=.../policy_sets/3p_monolithic_raw_pre_munge_rollup.jar` |
| `androidsupportmultidexversion.txt` | 53 | multidex 库版本戳：`git.version=eb8d5626...` |

另有 `jsr305_annotations/Jsr305_annotations.gwt.xml`（133 B），Google 注解库的 GWT 描述符，纯残留。

---

## 2. `AndroidManifest.xml` 里的组件

**权限**（1 个自定义声明 + 16 条 uses-permission）：`INTERNET`、`VIBRATE`、`READ_CONTACTS`、`GET_TASKS`、`READ_PHONE_STATE`、
`WRITE_EXTERNAL_STORAGE`(maxSdk 22)、`ACCESS_WIFI_STATE`、`CHANGE_WIFI_STATE`、`UPDATE_DEVICE_STATS`、
`ACCESS_NETWORK_STATE`、`CHANGE_NETWORK_STATE`、`..._c2dm.permission.RECEIVE`、`WAKE_LOCK`、
`com.android.vending.BILLING`、`FOREGROUND_SERVICE`、自定义 `permission.C2D_MESSAGE`（第 16 条，`protectionLevel=signature`）。
其中 `READ_CONTACTS` / `GET_TASKS` / `READ_PHONE_STATE` / `CHANGE_WIFI_STATE` / `UPDATE_DEVICE_STATS` 都是 Hive 与推送 SDK 要的，游戏本身不需要。

**关键组件**

| 类型 | 类 | 说明 |
|---|---|---|
| activity | `...android.common.SplashActivity` | 启动闪屏，`launchMode=singleTop`，强制横屏(`screenOrientation=11`) |
| activity | `...android.common.MainActivity` | 主 Activity（LAUNCHER），承载 `GLSurfaceView`；也接 `VIEW` scheme `com.com2us.inotia4.normal.freefull...` |
| service | `com.android.vending.billing.BillingService` | **旧版 Google Play 结算（Market Billing v2）**，非 AIDL v3 |
| receiver | `com.android.vending.billing.BillingReceiver` | 同上，收 `IN_APP_NOTIFY` / `RESPONSE_CODE` / `PURCHASE_STATE_CHANGED` |
| activity×3 | `com.com2us.module.push.ShowMsgActivity` / `NotificationMessage` / `PushWakeLock` | Com2uS 自家推送的展示页 |
| receiver×2 | `push.LocalPushReceiver`(`process=":remote"`) / `push.DeviceStatusReceiver` | 本地推送；开机/改时间时唤醒 |
| service | `com.com2us.module.fcm.MessagingService` | Firebase 推送（`com.google.firebase.MESSAGING_EVENT`） |
| service | `com.google.firebase.iid.FirebaseInstanceIdService` | FCM 注册 |
| receiver×2 | `FirebaseInstanceIdReceiver` / `FirebaseInstanceIdInternalReceiver` | C2DM 收消息 |
| provider | `com.google.firebase.provider.FirebaseInitProvider` | `initOrder=100`，**在 Application.onCreate 之前**由系统初始化，会拉起 Firebase 连 Google |
| receiver | `com.com2us.module.amazonpush.ADMMessageHandler$MessageAlertReceiver` | 亚马逊设备推送 |
| activity | `activeuser.useragree.AgreementUIActivity` | 条款/隐私同意页 |
| service | `activeuser.downloadcheck.InstallService` | 安装校验（校验包是否被改） |
| receiver | `activeuser.downloadcheck.InstallReceiver` | 收 `com.android.vending.INSTALL_REFERRER` |
| activity | `com.facebook.LoginActivity` / `FacebookActivity` / `CustomTabActivity` / `CustomTabMainActivity` | Facebook 登录/分享 |
| activity | `com.google.android.gms.auth.api.signin.internal.SignInHubActivity` | Google 登录 |
| activity | `com.google.android.gms.ads.AdActivity` | AdMob 广告位 |
| service×2 + receiver | `gms.cast.framework.media.MediaNotificationService` / `ReconnectionService` / `media.MediaIntentReceiver` | Cast 投屏 |
| service | `gms.tagmanager.TagManagerService` | Google Tag Manager |
| activity | `gms.appinvite.PreviewActivity` | Google 应用邀请 |
| activity | `gms.tagmanager.TagManagerPreviewActivity` | GTM 预览（`exported=true`，`noHistory`） |
| activity | `gms.common.api.GoogleApiActivity` | GMS 统一跳转壳 |
| service | `com.google.firebase.messaging.FirebaseMessagingService` | FCM 基础服务（被 `module.fcm.MessagingService` 继承） |
| service×2 | `gms.measurement.AppMeasurementService` / `AppMeasurementJobService` | Firebase Analytics 上报 |
| receiver×2 | `gms.measurement.AppMeasurementReceiver` / `AppMeasurementInstallReferrerReceiver` | Analytics 广播、安装来源 |
| service | `gms.auth.api.signin.RevocationBoundService` | Google 登录撤销通知 |
| meta-data×4 | `gms.games.APP_ID`、`gms.appstate.APP_ID`、`gms.version`、`facebook.sdk.ApplicationId` | 各 SDK 的 App ID（值在 `resources.arsc` 字符串池里） |

`uses-feature glEsVersion=0x20000`（OpenGL ES 2.0，但实际只用 ES 1.x 固定管线）。

---

## 3. `lib/armeabi-v7a/`（4 个）

| 文件 | 大小 | 用途 |
|---|---:|---|
| `libgame.so` | 1,725,020 | **全部游戏逻辑**：Com2uS 自研 GEM 引擎 + Inotia4 玩法 + Hub 网络 + 存档。详见 `SO_REVERSE.md` |
| `libfmodex.so` | 840,908 | FMOD Ex 音频核心（`FMOD_System_*` / `FMOD_Channel_*` / `FMOD_Sound_*`，754 个导出函数） |
| `libfmodevent.so` | 356,124 | FMOD Event System（`FMOD_EventSystem_*` / `FMOD_Event_*`，506 个导出函数），依赖 `libfmodex.so` |
| `关于so的改动.txt` | 52 | **重打包作者漏删的笔记**：`地址:从00012CCCA到00012CCDD全部改成NOP即可`（UTF-8，APK 内被 zip 存成 GBK 乱码名） |

三个 so 都是 32 位 ARM、EABI v5、gold 1.11 链接、GCC 4.8/4.6/4.4.3 混合编译。
`libgame.so` 的 `DT_NEEDED`：`libfmodex.so`、`libfmodevent.so`、`libGLESv1_CM.so`、`libdl.so`、`liblog.so`、`libstdc++.so`、`libm.so`、`libc.so`。

---

## 4. `assets/`（664 个）

### 4.1 `assets/sound/`（3 个，17.6 MB）

| 文件 | 大小 | 用途 |
|---|---:|---|
| `Inotia_03.fev.ogg` | 65,802 | FMOD Designer 工程文件，`FEV1` 魔数。定义 BGM/SE 事件、分组（`inotia_03/BGM`、`inotia_03/SE`） |
| `Inotia_03_bank00.fsb.ogg` | 6,160,928 | `FSB4` 音频包，169 个采样，mode=0x40(MPEG) |
| `Inotia_03_bank01.fsb.ogg` | 12,210,304 | `FSB4` 音频包，13 个采样（大体积 BGM），mode=0x40 |

假后缀 `.ogg` 同样是为了让 aapt 不压缩。`LoadFMOD`(0x68590) 初始化，`FMOD_EventSystem_SetMediaPath` 指向此目录。

### 4.2 `assets/common/game_res/`（549 个，20.5 MB）—— 游戏本体资源

| 类别 | 个数 | 体积 | 用途 |
|---|---:|---:|---|
| `m0.dat.jpg` … `m415.dat.jpg` | 416 | 831,527 B | 地图地形+图层，与 `MAPINFOBASE` 416 条一一对应 |
| `*.png.jpg` 图集 | 103 | 17,043,378 B | 全部 UI/立绘/图标，见 4.2.2 |
| `i_*.dat.jpg` 精灵包 | 19 | 2,269,999 B | 角色/怪物/特效/图块的精灵索引包，见 4.2.3 |
| `memorytext*.dat.jpg` | 8 | 1,254,674 B | 全部文本（8 种语言） |
| `eventdata.dat.jpg` | 1 | 75,683 B | 剧情脚本（28598 条 × 10 B） |
| `game.dat.jpg` | 1 | 46,884 B | 100 张玩法数据表 |
| `font.fnt.jpg` | 1 | 3,960 B | BMFont 位图字体描述 |

#### 4.2.1 容器格式（所有 `.dat.jpg`）

```
[0]     u8  0x01 = LZMA 压缩；非 0x01 表示原样（i_portrait / i_title / i_worldmap 是原始）
[1]     u8  0x00
[2..6]  5B  LZMA 属性  5d 00 00 00 01 → lc3 lp0 pb2, 字典 16 MB
[7..10] u32 解压后字节数
[11..14] u32 保留 0
[15..]  LZMA1 裸流（FORMAT_RAW）
```
> 坑：`LZMA_DecodeEx` 内部把 `src+14` 当数据起点、`src+1` 当属性起点，而调用方传的是「文件指针+1」，所以文档里的偏移整体 +1。

#### 4.2.2 103 张图集（`*.png.jpg`）

真实格式就是 PNG（8 bit RGBA），1024×1024 或 512×512 的图集，索引由 `libgame.so` 的 `.data:0x1A102C` 98 项表驱动（见 `SO_REVERSE.md` §7）。

| 前缀 | 张数 | 用途 |
|---|---:|---|
| `UI_003`–`UI_015` | 12 | 通用界面元件：按钮底、边框、卷轴、进度条、圆环、箭头、货币图标 |
| `UI_BUT_ENG/KOR/JP/cn_s/cn_t/FRA/GER_001`–`005` | 35 | **7 种语言各 5 张**的按钮文字图集（中简/中繁/英/日/韩/法/德） |
| `UI_TITLEMAIN_BUT_*_001` | 7 | 各语言的主菜单大按钮 |
| `UI_TITLEMAIN_01/02` | 2 | 标题画面：01 含背景魔法阵、INOTIA 龙 logo、HIVE 按钮、喇叭按钮、底部版权行；02 是第二层 |
| `UI_TITLESLOT_01` | 1 | 存档槽界面底图 |
| `UI_TITLE_SETTINGS_01` | 1 | 设置页底图 |
| `UI_iconskill75_001`–`004` | 4 | 技能图标图集（75px 网格） |
| `UI_portrait_status_001`–`006` | 6 | 立绘状态框 |
| `UI_shopicon_001/002` | 2 | 商店分类图标 |
| `UI_cashitem88icon` | 1 | 内购商品角标 |
| `portrait_001`–`014` | 14 | 剧情立绘（大图，单张 200–330 KB） |
| `item_001`–`009` | 9 | 掉落物/物品图标图集 |
| `ETC_ban_001/002/002_K` | 3 | 内购推广横幅（"GET NO ADS VER. +100 GEMS"、"FREE GEM"），韩文版 |
| `Default` | 1 | com2uS 红色 logo（380×60），启动画面用 |
| `GRB` | 1 | 韩国游戏分级标识（GRB 全年龄 + 暴力性），410×232 |
| `Googleplay` | 1 | Google Play 徽标，1200×640 |
| `UI_BUT_RightCancel` / `UI_BUT_Probability` | 2 | 单按钮素材（取消、概率公示） |

`Default/GRB/Googleplay/ETC_ban_002/ETC_ban_002_K` 这 5 张不在 98 项索引表里，是代码直接按文件名加载的。

#### 4.2.3 19 个精灵包（`i_*.dat.jpg`）

```
[0..3]   u32 count（注意：i_portrait 的这个值 3328 是「精灵总数」，不是段数）
[4..17]  14 B 头（各包不同，含义未定）
[18..]   (n+1) × 3B 偏移，bit23(0x800000) = 该段 LZMA 压缩
段 i = [(off[i] & 0x7FFFFF) + delta, (off[i+1] & 0x7FFFFF) + delta)
```
两处实测偏差（`tools/re_pack.py` 会自动探测）：
- 表起点不固定：多数包 18，`i_portrait` 是 16（前面还有 60 字节头）
- `delta` 不固定：多数包 0，`i_item`/`i_npc`/`i_tile`/`i_effect`/`i_efx`/`i_imoticon`/`i_portrait` 是 1
  ——与 LZMA 容器「调用方传文件指针+1」是同一个 off-by-one

段内还有一层 LZMA 容器（13 字节头 + 裸流：`5B props + u32 解压长度 + u32 保留`），
`i_portrait` 的段多一个前导 `0x00`，流从 +14 开始。

每段解压后是一个 SNASYS "pack chunk"，由 `SNASYS_ReadObjectCountAll`(0xC6720) 统一读出 sprite / frame / animation / palette / costume / mani 六类对象。

| 文件 | u32 | 段数 | 压缩段 | 解压后 | 用途 |
|---|---:|---:|---:|---:|---|
| `i_effect2` | 2069 | 2069 | 556 | 1,628,966 | 特效（第二组，量最大） |
| `i_effect` | 1602 | 1602 | 353 | 895,888 | 特效（第一组） |
| `i_player` | 4399 | 4399 | 2108 | 841,904 | 玩家 6 职业 × 男女 × 各动作帧 |
| `i_monster` | 2265 | 2265 | 835 | 602,372 | 普通怪物 |
| `i_tile` | 1983 | 1983 | 1864 | 470,311 | 地图图块（tile id 0..1880 与 `m*.dat` 对应） |
| `i_ui_gamemenu` | 871 | 871 | 200 | 417,699 | 游戏内菜单元件 |
| `i_mapfeature` | 213 | 213 | 162 | 264,698 | 地图装饰物（树/石/建筑） |
| `i_portrait` | 3328 | 14 | 12 | 223,677 | 立绘切片（`u32@0=3328` 应是精灵总数） |
| `i_worldmap` | 94 | 94 | 94 | 218,264 | 世界地图 |
| `i_monster_boss` | 182 | 182 | 86 | 187,664 | Boss 专属 |
| `i_item` | 362 | 362 | 290 | 127,810 | 物品图标 |
| `i_npc` | 394 | 394 | 137 | 106,809 | NPC |
| `i_title` | 6 | 6 | 6 | 67,795 | 标题画面图层 |
| `i_skillicon` | 204 | 204 | 131 | 58,392 | 技能图标 |
| `i_efx` | 42 | 42 | 29 | 44,870 | 特效参数 |
| `i_map_image` | 25 | 25 | 6 | 27,009 | 小地图底图 |
| `i_sign` | 40 | 40 | 13 | 8,932 | 路牌/招牌 |
| `i_imoticon` | 84 | 84 | 4 | 4,318 | 聊天表情 |
| `i_imgfont` | 86 | 86 | 0 | 1,756 | 图片数字字模（伤害数字） |

合计 **14,935 段，14,935 段全部解压成功**，其中 6,886 段是二次压缩，解压总量 **6,199,134 B**。

#### 4.2.4 文本表 `memorytext*.dat.jpg`（8 个）

```
[u32 count][count × 3B 绝对偏移][NUL 分隔 UTF-8 池]，池起点 = 4 + count*3
```
`&N` 是换行转义，`$S…$B` / `$R…$B` 是颜色/描边标记。**物品名索引 == 物品 ID（≥30）**，所以查物品中文名直接 `texts[item_id]`。

| 文件 | 条数 | 解压后 | 用途 |
|---|---:|---:|---|
| `memorytext.dat` | 35,812 | 775,188 B | 默认（= 韩文/英文回退） |
| `memorytext_zhhans.dat` | 35,811 | 649,217 B | 简体中文。**已改**（35456/35457 两条文案，文件 177,172→177,028 B） |
| `memorytext_zhhant.dat` | 35,811 | 649,376 B | 繁体中文 |
| `memorytext_en.dat` | 35,812 | 619,103 B | 英文 |
| `memorytext_jp.dat` | 35,811 | 800,582 B | 日文 |
| `memorytext_fr.dat` | 35,811 | 665,373 B | 法文 |
| `memorytext_de.dat` | 35,811 | 664,032 B | 德文 |
| `memorytext_e.dat` | 1,991 | 18,923 B | **英文事件/调试专用短表**（`MEMORYTEXT_GetText_E` 专用） |

语言由 `GetConfigLanguage`(0xAB084) 返回 `eng/kor/zhc/zht/jpn/fra/ger`，再拼成文件名。

#### 4.2.5 `game.dat.jpg`（46,884 B）—— 100 张玩法表

```
[u32 count=100][1 B 0][100 × 3B 累计结束偏移][子表…][末尾 1 B 0]
数据起点 = 305
每张子表 = [u32 count][u16 记录字节数][记录…]
```
子表顺序 = `.dynsym` 里 `XXX_nRecordCount` 的符号索引序。全部 100 张已核对记录长度与字节数严格吻合。**当前为原始数据，逐字节未改**（MD5 `fb3d6f77cae53eba8e6cbcaabdc55c8d`）。

关键表：

| 索引 | 表 | 规模 | 用途 |
|---:|---|---|---|
| 0 | CHARCLASSBASE | 6×20 | 6 个职业（男女各 3） |
| 13 | ITEMDATABASE | 1018×23 | 全部物品；`+0 u16 物品ID`、`+12 u16 价格`（铜币） |
| 25 | DEALINFOBASE | 78×8 | NPC 货架：`+0 npc`/`+2 槽位`/`+3 类型`/`+4 p1`/`+6 p2` |
| 28 | ACTDATABASE | 192×32 | 动作/技能定义 |
| 29 | ACTUNITBASE | 267×21 | 动作判定单元 |
| 34 | SKILLDESCBASE | 114×24 | 技能描述 |
| 43 | ANIMATIONDEFINEBASE | 334×25 | 动画定义 |
| 47 | MONDATABASE | 553×39 | 怪物 |
| 51 | NPCINFOBASE | 656×10 | NPC；`+0 u16` = 名字的 memorytext 索引；**行号 == DEALINFOBASE 的 npc id** |
| 58 | MAPINFOBASE | 416×11 | 地图；`+0 u16` 名字索引、`+2 u16` 宽、`+4 u16` 高 |
| 59 | EVTINFOBASE | 608×8 | 剧情事件索引；`+4 u16` 起始记录、`+6 u16` 条数 |
| 60 | EVTCONDBASE | 873×7 | 事件触发条件 |
| 65 | QUESTINFOBASE | 507×32 | 任务；`+0x0a u16 经验`、`+0x1a u16 奖励起始`、`+0x1c u8 奖励数` |
| 70 | QUESTREWARDBASE | 394×5 | 任务奖励；`+0 u16` **真实物品 ID = 值 + 30** |
| 73 | TEXTDATABASE | 190×2 | 消息 id → memorytext 索引（`0x20`=购买成功、`0x8C`=能力点初始化…） |
| 75 | MONSTERDROPBASE | 160×6 | 怪物掉落组 |
| 78 | DROPDETAILINFOBASE | 158×8 | 掉率，单位 1/2500 |
| 85 | CASHITEMBASE | 44×8 | 内购商品（未改动） |

#### 4.2.6 `eventdata.dat.jpg`（75,683 B）—— 剧情脚本

```
[u32 count=28598][u16 recsize=10][28598 × 10 B]
每条：u8 命令(1/2/3) + u16 参数… + u16 累计序号（3929..32526，严格单调）
```
由 `EVTSYSTEM_LoadEvent`(0xA66F8) 按 `EVTINFOBASE` 的「起始+条数」切片加载。命令种类只有 3 种（`EVTCMDBASE` 78 条全是 1/2/3），文本内容全在 `memorytext`。

#### 4.2.7 `m*.dat.jpg` —— 416 张地图（831,527 B）

```
[0] u8 b0（保留）  [1] u8 b1（保留）  [2] u8 w  [3] u8 h
[4..]  w*h × 2B 地形层：tile = ((b0 & 0x0F) << 8) | b1     （12 位 tile id）
之后 MAP_LoadLayer：
    u16 layerType
    4 × u16 图层参数 → MAPFEATURESYSTEM_AddLayer
    u8 blockCount
    每 block：u8（高 4 位 = 层类型 0..3 → AddLayerTile/Shadow1/Shadow2/TopTile）
              u16 记录数
              每条 4B：u8 dx / u8 dy / u8 packed / u8 tileLo
                       packed: bit0-2 = tile 高位, bit3 = 0x800 标记, bit4-7 = 类型(0..5)
```
实测 m0 = 25×30=750 格 + 7 个 block（1/39/267/0/20/4/14 条记录），尾部剩 25 B 未解析（应为事件 id 表）。
地形 tile id 实测范围 0..1880，正好落在 `i_tile` 的 1983 个段内。

---

### 4.3 `assets/common/ActiveUser*`（65 个，392 KB）—— 合规/更新/公告

| 目录 | 内容 |
|---|---|
| `ActiveUserAgreement/` | 5 个 HTML 条款页 + `checked.png` 勾选图 + 两个 `au_terms_meta.json` 索引 |
| `ActiveUserDialog/` | 24 张弹窗九宫格素材：维护公告、强制更新、封号、滚动条、倒计时框（pad/phone 两套） |
| `ActiveUserImage/` | 28 张 + `checkPermission/` 5 张：com2us/gamevil 双 logo 的对话框边框、按钮、权限申请插画 |

`au_terms_meta.json` 按 MCC 分地区选条款：

| MCC | 地区 | 版本 | 文件 |
|---|---:|---:|---|
| 450 | KR | 6 | `1474439205_M8_6.html` |
| 440 | JP | 4 | `1417773035_M10_4.html` |
| 460 | CN | 5 | `1417772988_M11_5.html` |
| 466/454/455 | TW/HK/MO | 8 | `1417600874_M12_8.html` |
| — | gamevil | 11 | `14110301_TEST01_1.html` |

### 4.4 `assets/common/mercury/`（40 个）—— 推广/评价弹窗

com2uS "Mercury" 模块的素材：`popup_box_review_*`（求好评）、`popup_box_end_*`（退出挽留）、
`popup_box_*_touch_*`（按钮）、`0_wide_outline` / `0_length_outline`（外框）、`btn_native_X`（关闭）。
服务器已下线，实际不会弹。

### 4.5 `assets/common/offerwall/`（2 个）

`btn_native_X.png` / `btn_native_X_tablet.png`，Offerwall（积分墙广告）的关闭按钮。

### 4.6 `assets/` 根目录（5 个）

| 文件 | 大小 | 用途 |
|---|---:|---|
| `com.tencent.open.config.json` | 56 | QQ 互联 SDK 配置：`Common_ta_enable=1`、`Common_frequency=24`（腾讯统计上报开关） |
| `libwbsafeedit` | 17,592 | 微博 SDK 的 native 库，**ELF ARM 32 位** |
| `libwbsafeedit_64` | 13,776 | ELF AArch64 |
| `libwbsafeedit_x86` | 9,352 | ELF x86 32 位 |
| `libwbsafeedit_x86_64` | 14,016 | ELF x86-64 |

这 4 个是微博分享 SDK 的 `.so`，本该放 `lib/` 下，被错误打进 `assets/`，**运行时永远不会被加载**，纯冗余。

---

## 5. `res/`（1388 个，2.6 MB）—— 全部是第三方 SDK 的 Android 资源

游戏自己的 UI 全部画在 `libgame.so` 里（`SurfaceView` + OpenGL），所以 `res/` 里**没有一张游戏界面图**，全是依赖库的资源。

| 家族 | 张数 | 来源 |
|---|---:|---|
| `ic_*`（`ic_mr_button_*`、`ic_group_*`、`ic_media_*`…） | 341 | Google Cast / MediaRouter |
| `abc_*` + `layout/abc_*` | 271 + 41 | AppCompat v7 27.1.0 |
| `quantum_ic_*` | 205 | Google Cast 的 Material 图标 |
| `cast_*` + `layout/cast_*` | 88 + 9 | Cast SDK |
| `com_facebook_*` / `com_facebook_*` 布局 | 64 + 9 | Facebook SDK |
| `hive_*` / `hivedialog_*` / `hub_*` | 40 + 2 + 1 | Com2uS Hive 平台弹窗 |
| `promotion_*` + 布局 | 33 + 17 | Hive 推广位（横幅/全屏/视频） |
| `messenger_*` + 布局 | 24 + 6 | Facebook Messenger 分享 |
| `activeuser_*` + 布局 | 10 + 18 | Com2uS 公告/更新弹窗 |
| `notification_*` + 布局 | 17 + 14 | support-compat 通知模板 |
| `mr_*` + 布局 | 18 + 6 | MediaRouter |
| `common_google_signin_*` / `googleg_*` / `powered_by_google_*` / `places_*` | 35 / 8 / 10 / 10 | Google 登录、Play Games、Places |
| `ic_launcher*`（mipmap，5 套 dpi + adaptive icon xml） | 20 | 应用图标 |
| `res/raw/google_splash_video.mp4` | 1 | 662,796 B，Google 启动动画视频 |
| `res/raw/mraid.js` | 1 | 13,764 B，MRAID 广告 JS |
| `res/xml/config.xml` `hive.xml` `hubjoin.xml` `clause.xml` | 4 | Hive SDK 配置（10.6 KB / 3.1 KB / 3.3 KB / 9.9 KB） |
| `res/anim/`、`res/color/`、`res/interpolator/`、`res/layout/main.xml` | 12 / 38 / 2 / 1 | 动画、颜色状态、插值器；`main.xml` 是**游戏唯一自绘布局**：`RelativeLayout` > `GLSurfaceView` + 2 个 `FrameLayout`（输入框、加载遮罩） |

---

## 6. `META-INF/`（16 个）

| 文件 | 说明 |
|---|---|
| `MANIFEST.MF` (211,771) | v1 摘要清单 |
| `INOTIA.SF` (211,822) | v1 签名文件 |
| `INOTIA.RSA` (1,189) | v1 签名块（RSA 2048，CN=InotiaMod） |
| `*.version` × 13 | 各 support/android.arch 库的版本戳（27.1.0、1.1.0 等），Gradle 自动生成 |

v2/v3 签名在 APK 尾部的签名块里，不在 zip 条目中。原包是 `ANDROID.SF`/`ANDROID.RSA`，重签时被 apksigner 换掉。

---

## 7. 本包相对基线的全部差异（逐字节核对）

| 条目 | 基线 | 成品 | 说明 |
|---|---:|---:|---|
| `classes.dex` | 8,994,848 | 8,994,896 | +48 B：注入固定 DID + 50 处 `qpyou.cn`→`qpyou.zz` |
| `lib/armeabi-v7a/libgame.so` | 1,725,020 | 1,725,020 | 等长，**37 字节**差异，10 处补丁 |
| `assets/common/game_res/UI_TITLEMAIN_01.png.jpg` | 659,873 | 771,629 | 擦 3 块（版权行/HIVE/喇叭），重编码后 +111 KB |
| `assets/common/game_res/memorytext_zhhans.dat.jpg` | 177,172 | 177,028 | 改 2 条文案，字符串池缩短 |
| `META-INF/MANIFEST.MF` `ANDROID.*`→`INOTIA.*` | — | — | 重签名 |
| 其余 2076 个条目 | — | — | **逐字节一致**（含 `game.dat`、`eventdata.dat`、全部地图与图集） |

---

## 8. 运行时才会出现、包内没有的文件

| 文件 | 位置 | 用途 |
|---|---|---|
| `save0.dat`–`save2.dat` | `/sdcard/Android/data/com.com2us.inotia4.cat/files/<MD5(包名)>/` | 3 个存档槽 |
| `appinfo.dat` | 同上 | `APPINFO_Load`/`APPINFO_Save`(0x9451C/0x945DC) 读写：语言、音量、画质等设置 |
| `envinfo.dat` | 同上 | 环境信息缓存（so 里只有字符串，无独立符号，疑在 `APPINFO_*` 内一并处理） |
| `i_pack.dat` | 同上 | `FILEPACK_Load/Create`(0xA99C0/0xA9A80)：已下载/安装的资源包索引 |
| `filetext.dat` | 同上 | `FILETEXT_Load`(0xA9D08)：文件文本缓存 |
| `thpcheckslot1/2/3.dat` | 同上 | `Tutorialdatasave/load`(0xE5C60/0xE5D98)：新手引导进度 |
| `texture.store` | `/sdcard/Android/data/<pkg>/files/` | `textureStore` 类（0x12F9E8 起）的 GL 纹理缓存 |
| `SND/SND_LIST.txt` | `/sdcard/Android/data/<pkg>/files/SND/` | 声音列表缓存 |
| `StoreConFirm.sav` | 数据目录 | 商店确认状态（so 里只有文件名常量，无独立读写函数） |

存档目录名不是包名本身，而是 `HubSave_GetFolderName`(0x6972C) 算的 **MD5(包名) 十六进制**（16 字节 → 32 字符）；
对当前包名就是 **`d332b56cf8a2b9c51bbe7f431ce6602f`**（可用 `hashlib.md5(b"com.com2us.inotia4.cat")` 复算）。
`HubSave_GetKey`/`HubSave_LoadKey`(0x69660/0x69670) 管理密钥。存档内容用 `ENCRYPT_Process2`(0x755F0) 加密：
逐字节 XOR + `ENCRYPT_GetKey`(0x755E4) 推进的密钥流 + 尾部随机种子。

---

## 9. 快速索引：想改什么看哪里

| 想改 | 改哪个文件 | 工具 |
|---|---|---|
| 物品价格 / 商店货架 / 任务奖励 | `game.dat.jpg`（第 13/25/65/70 张子表） | `tools/inotia_edit.py` |
| 剧情台词 / 物品名 / 提示文案 | `memorytext_zhhans.dat.jpg` | `tools/inotia_dat.py` + `tools/patch_text_reset.py` |
| 剧情流程 | `eventdata.dat.jpg` + `EVTINFOBASE` | `tools/inotia_dat.py` |
| 地图地形 | `m*.dat.jpg` | `tools/re_map.py` |
| 角色/怪物/特效外观 | `i_*.dat.jpg`（精灵包）+ 对应 `*.png.jpg` 图集 | `tools/re_pack.py` |
| 界面按钮位置/尺寸 | `libgame.so` 的 `UIPlay_Create` 等 | `tools/extract_ui.py` + `ui_preview/ui_editor.html` |
| 界面美术 | 对应 `*.png.jpg` 图集 | `tools/pngtool.py` |
| 数值平衡（经验/掉率/属性） | `game.dat.jpg` 或 `libgame.so` 里的常量 | `tools/inotia_edit.py` / `tools/armdis.py` |
| 启动速度 / 联网行为 | `classes.dex` | `tools/baksmali.jar` + `smali.jar` |
