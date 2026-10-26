# 开源参考与实现选择

本项目独立使用 C# / WPF 实现，没有打包下列项目的代码、图标或运行时。这些项目用于参考功能与交互设计。

- Stocks Widget — https://github.com/Noam13-w/stocks-widget
  - MIT，许可证：https://raw.githubusercontent.com/Noam13-w/stocks-widget/main/LICENSE
  - 查看了 main.js 的无边框置顶窗口、窗口尺寸与位置保存、到价触发后持久化停用状态。
  - 本项目采用相同的常见交互原则，并增加 USDT 永续行情、过期标识、短时涨跌和独立方向冷却。
- Cryptonose — https://github.com/dawidm/cryptonose2
  - 仓库标示 MIT；功能说明：https://dawidm.github.io/cryptonose2/
  - 参考其短时间窗口涨跌监控和桌面提醒思路。本项目按用户设置的秒数比较窗口起点与当前成交价。
- Crypto Widget — https://github.com/WebStudio55/crypto-widget
  - 仓库标示 GPL-3.0，采用 Python / PyQt6；README 展示浮窗、透明度和置顶设计。
  - 该项目使用现货接口；本项目使用合约接口。没有复制其代码或图片。

接口依据：
- 币安 USDT 合约 WebSocket：https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/ws-streams/market
- 币安交易对信息：https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/rest-api/market-data
- TradingView 永续代码：https://www.tradingview.com/symbols/BTCUSDT.P/

参考核对日期：2026-10-24。
