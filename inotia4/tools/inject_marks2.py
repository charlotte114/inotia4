import os

SM = r"D:\Desktop\inotia4\smali_out"

TARGETS = [
    (
        "com/com2us/module/offerwall/Offerwall.smali",
        ".method public constructor <init>(Landroid/app/Activity;)V",
        [
            ("LoggerGroup;->createLogger", 40),
            ("OfferwallData;->create", 41),
            ("NetworkTimeoutProperties;->setContext", 42),
            ("NetworkTimeChecker;-><init>", 43),
            ("ModuleManager;->getDatas", 44),
        ],
    ),
    (
        "com/com2us/module/mercury/Mercury.smali",
        ".method private initialize(Landroid/app/Activity;)V",
        [
            ("PropertyUtil;->loadProperty", 45),
            ("LoggerGroup;->createLogger", 46),
            ("NetworkTimeoutProperties;->setContext", 47),
            ("NetworkTimeChecker;-><init>", 48),
            ("ModuleManager;->getDatas", 49),
            ("MercuryProperties;->loadProperties", 50),
            ("MercuryData;->create", 51),
            ("CookieSyncManager;->createInstance", 52),
            ("CookieManager;->getInstance", 53),
            ("Mercury;->deleteExpiredPID", 54),
        ],
    ),
]


def extend_t():
    p = os.path.join(SM, "com", "x", "T.smali")
    s = open(p, encoding="utf-8").read().rstrip()
    add = []
    for rel, _sig, pairs in TARGETS:
        cls = os.path.basename(rel).replace(".smali", "")
        for pat, mk in pairs:
            label = pat.split(";->")[-1]
            add += [
                ".method public static a%d()V" % mk,
                "    .locals 2",
                '    const-string v0, "TIMING"',
                '    const-string v1, "at%d-%s.%s"' % (mk, cls, label),
                "    invoke-static {v0, v1}, Landroid/util/Log;->e(Ljava/lang/String;Ljava/lang/String;)I",
                "    return-void",
                ".end method",
                "",
            ]
    open(p, "w", encoding="utf-8").write(s + "\n\n" + "\n".join(add))
    print("T 已扩展")


def insert(rel, sig, pairs):
    fp = os.path.join(SM, rel)
    lines = open(fp, encoding="utf-8").read().split("\n")
    st = None
    for i, l in enumerate(lines):
        if l.startswith(sig):
            st = i
            break
    if st is None:
        print("  [跳过方法]", sig)
        return
    en = None
    for j in range(st + 1, len(lines)):
        if lines[j].startswith(".end method"):
            en = j
            break
    ins = []
    for pat, mk in pairs:
        hit = None
        for j in range(st, en):
            if pat in lines[j] and lines[j].strip().startswith("invoke"):
                hit = j
                break
        if hit is None:
            print("    [跳过]", pat)
            continue
        ins.append((hit, mk, pat))
    for hit, mk, pat in sorted(ins, key=lambda x: -x[0]):
        lines.insert(hit, "    invoke-static {}, Lcom/x/T;->a%d()V" % mk)
    for hit, mk, pat in sorted(ins):
        print("  %s: a%d -> %s" % (os.path.basename(rel), mk, pat))
    open(fp, "w", encoding="utf-8").write("\n".join(lines))


if __name__ == "__main__":
    extend_t()
    for rel, sig, pairs in TARGETS:
        insert(rel, sig, pairs)
