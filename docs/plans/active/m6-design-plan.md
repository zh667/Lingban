# M6 操作台设计方案(frontend-design 两遍法产物)
主题:安灯语言的对话式领班。单列对话流(max 760px),用户右侧简气泡,助手无气泡+左缘 3px 安灯灯柱(绿/黄/红=本轮最差校验态)。
Tokens:bg #171D26 / panel #1F2733 / text #E8ECF1 / brass #C9A15A(身份与 HITL 边框)/ andon 绿 #3FA36C 黄 #D9A62E 红 #C4554D(仅语义)。
字体:标题 Songti SC/Noto Serif CJK SC;正文 PingFang SC/Noto Sans SC;数据与 SQL ui-monospace。
组件:工具卡(眉标=工具名+安灯点+耗时;展开区 toolSql/verificationSql 等宽);引用胶囊 [文档§章节] 可点;answer_audit 失败=消息尾红条列出未证实数字/伪造引用;HITL 确认卡(黄铜描边,"执行报工/取消"双钮,倒计时无——等人)。
反俗套自查:非 cream+serif+terracotta、非近黑+荧光绿(深靛+黄铜+三色语义)、非 broadsheet 细线。签名=安灯灯柱,唯一大胆处,其余克制。
质量地板:键盘焦点可见、prefers-reduced-motion、移动端可用、双语目录(zh-CN 主/en-US 同步)。
