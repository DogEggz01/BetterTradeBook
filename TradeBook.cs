using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace BetterTradeBook
{
    // ---- Trade-book trading (EconomyOverhaul's NewMarket.cs, on the game's own prices) ----
    // Each unit is priced as the game prices it when bought or sold one at a time: IslandMarket.GetBuyPrice / GetSellPrice at
    // the supply that unit would meet, in the player's currency through CurrencyMarket (with the game's conversion fee when the
    // currency is not the port's region's), exactly as EconomyUI.BuyGood / SellGood do.
    internal enum TradeAction { Buy, Sell }
    internal sealed class TradeUnit
    {
        internal Good Cargo;
        internal int Price;
    }
    internal sealed class TradeBatch
    {
        internal TradeAction Action;
        internal IslandMarket Market;
        internal int Good, Currency, Count;
        internal string Error;
        internal CargoCarrier Carrier;
        internal int CartCurrency, CartUnitFee;
        internal long SaleCartFee, StorageFee;
        internal long CartFee => Action == TradeAction.Sell ? SaleCartFee : (long)CartUnitFee * Count;
        internal long CartCharge => CartFee + StorageFee;
        internal List<TradeUnit> Units = new List<TradeUnit>();
        internal bool Valid => Error == null && Units.Count == Count;
        internal long Total => Units.Sum(u => (long)u.Price);
        internal long NetPayout => Total - (Carrier && CartCurrency == Currency ? CartCharge : 0);
        internal string Breakdown()
        {
            if (!Valid) return Error ?? "Cargo unavailable";
            string coin = PlayerGold.GetCurrencyName(Currency);
            if (Action == TradeAction.Sell)
            {
                string text = "Sale value: " + Total.ToString("N0") + " " + coin;
                if (Carrier)
                {
                    string cartCoin = PlayerGold.GetCurrencyName(CartCurrency);
                    text += "\nCart selling (20x): " + CartFee.ToString("N0") + " " + cartCoin + "\nStorage: " + StorageFee.ToString("N0") + " " + cartCoin;
                    text += "\nCart cash required upfront: " + CartCharge.ToString("N0") + " " + cartCoin;
                }
                text += "\nReceive: " + NetPayout.ToString("N0") + " " + coin;
                if (Carrier && CartCurrency != Currency) text += "\nSeparate cart debit: " + CartCharge.ToString("N0") + " " + PlayerGold.GetCurrencyName(CartCurrency);
                return text;
            }
            string result = "Cargo cost: " + Total.ToString("N0") + " " + coin;
            if (Carrier) result += "\nCart loading (10x): " + CartFee.ToString("N0") + " " + PlayerGold.GetCurrencyName(CartCurrency);
            result += "\nCash required: " + (Total + (Carrier && CartCurrency == Currency ? CartFee : 0)).ToString("N0") + " " + coin;
            if (Carrier && CartCurrency != Currency) result += " + " + CartFee.ToString("N0") + " " + PlayerGold.GetCurrencyName(CartCurrency);
            return result;
        }
    }
    internal static class BulkTrade
    {
        internal const int MaxBatch = 20;
        // While a batch runs, the game's per-unit RefreshPage calls wait (one refresh at the end).
        internal static bool Executing;
        internal static int KeyQuantity(bool alt, bool ctrl, bool shift) => shift ? 20 : ctrl ? 10 : alt ? 5 : 1;
        internal static int HeldQuantity() => KeyQuantity(Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt),
            Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl), Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
        internal static int Quantity(GoPointerButton button) => ButtonHover.IsOver(button) ? HeldQuantity() : 1;
        internal static IEnumerable<Good> SaleGoods(IslandMarket market, int goodIndex, CargoCarrier carrier = null)
        {
            var area = market.GetWarehouseArea();
            if (!area) return Enumerable.Empty<Good>();
            IEnumerable<Good> source = carrier ? carrier.cargo.Where(item => item && item.GetCurrentInventorySlot() == carrier.portIndex + 100).Select(item => item.GetComponent<Good>()) :
                Fields.Get<List<Good>>(area, "goodsInArea").Where(g => g && g.GetComponent<ShipItem>().GetCurrentInventorySlot() < 100);
            return source.Where(g => g && g.GetComponent<ShipItem>().sold && g.GetMissionIndex() == -1 &&
                PrefabsDirectory.ItemToGoodIndex(g.GetComponent<SaveablePrefab>().prefabIndex) == goodIndex && (bool)Fields.Call(area, "IsGoodValid", g)).Distinct();
        }
        // The game's conversion fee: when the port's region is not the player's currency (EconomyUI.GetBuyPrice).
        internal static bool ConversionFee(IslandMarket m, int currency) => m.GetPort().region != (PortRegion)currency;
        internal static TradeBatch Quote(EconomyUI ui, TradeAction action, int count)
        {
            var batch = new TradeBatch { Action = action, Count = count, Market = Fields.Get<IslandMarket>(ui, "currentIsland"), Good = ui.currentSelectedGood };
            var m = batch.Market;
            if (GameState.currentlyLoading || !m || !CurrencyMarket.instance || PlayerGold.currency == null || count < 1 || count > MaxBatch) { batch.Error = "Trade book unavailable"; return batch; }
            batch.Currency = (int)Fields.Get<Currency>(ui, "currentPlayerCurrency");
            if (batch.Currency != m.GetPortRegion() && !m.allowCurrencyConversion) { batch.Error = "Wrong currency"; return batch; }
            int g = batch.Good;
            var prefab = PrefabsDirectory.instance.GetGood(g);
            if (!prefab || g <= 0 || g == 45 || g == 51) { batch.Error = "Cargo unavailable"; return batch; }
            bool buy = action == TradeAction.Buy;
            // The game's own purchase test: the stock limit and a good sold only at its port of origin (Saffron).
            if (buy && !m.HasGood(g)) { batch.Error = "Not enough stock"; return batch; }
            long[] wallets = PlayerGold.currency.Select(x => (long)x).ToArray();
            var eligible = new List<Good>();
            float supply = m.currentSupply[g];
            try
            {
                if (ui.GetComponent<BulkTradeUI>() && ui.GetComponent<BulkTradeUI>().CartSelected)
                {
                    batch.Carrier = CartDelivery.Hired(m, out batch.Error); if (!batch.Carrier) return batch;
                    batch.CartCurrency = (int)batch.Carrier.currency;
                }
                if (!buy)
                {
                    eligible = SaleGoods(m, g, batch.Carrier).Take(count).ToList();
                    if (eligible.Count < count) { batch.Error = "Not enough " + (batch.Carrier ? "cart" : "warehouse") + " cargo for the full batch"; return batch; }
                }
                if (batch.Carrier)
                {
                    if (!buy) foreach (var good in eligible)
                    {
                        var item = good.GetComponent<ShipItem>();
                        batch.SaleCartFee += CartDelivery.Fee(batch.Carrier, item, 20);
                        int storage = batch.Carrier.GetWithdrawPrice(batch.Carrier.cargo.IndexOf(item));
                        if (storage < 0) throw new OverflowException("Storage fee exceeds wallet limit");
                        batch.StorageFee += storage;
                    }
                    else batch.CartUnitFee = CartDelivery.Fee(batch.Carrier, prefab.GetComponent<ShipItem>());
                    if (batch.CartCharge > int.MaxValue) throw new OverflowException("Cart fee exceeds wallet limit");
                    wallets[batch.CartCurrency] -= batch.CartCharge;
                    if (wallets[batch.CartCurrency] < 0) { batch.Error = "Not enough cash for cart fees of the full order"; return batch; }
                }
                bool fee = ConversionFee(m, batch.Currency);
                for (int i = 0; i < count; i++)
                {
                    // The supply this unit meets; the market's own price at it (restored below).
                    m.currentSupply[g] = supply + (buy ? -i : i);
                    if (buy && m.currentSupply[g] < m.supplyPurchaseLimit) { batch.Error = "Not enough stock for the full batch"; return batch; }
                    int raw = buy ? m.GetBuyPrice(g) : m.GetSellPrice(g);
                    int price = buy ? CurrencyMarket.instance.GetBuyPriceInCurrency((Currency)batch.Currency, raw, fee)
                        : CurrencyMarket.instance.GetSellPriceInCurrency((Currency)batch.Currency, raw, fee);
                    wallets[batch.Currency] += buy ? -price : price;
                    if (buy && wallets[batch.Currency] < 0) { batch.Error = "Not enough money for the full batch"; return batch; }
                    if (wallets.Any(v => v < int.MinValue || v > int.MaxValue)) { batch.Error = "Wallet limit exceeded"; return batch; }
                    batch.Units.Add(new TradeUnit { Cargo = buy ? null : eligible[i], Price = price });
                }
            }
            catch (OverflowException) { batch.Error = "Price or wallet limit exceeded"; }
            finally { m.currentSupply[g] = supply; }
            return batch;
        }
        internal static void Execute(EconomyUI ui, TradeAction action, int count)
        {
            var batch = Quote(ui, action, count);
            if (!batch.Valid) { Notice.Show(batch.Error ?? "Cargo unavailable"); ui.RefreshPage(); return; }
            var m = batch.Market; int g = batch.Good, currency = batch.Currency;
            // Stage every physical purchase before touching money or stock (as IslandMarket.SpawnGood makes one).
            var staged = new List<Good>();
            if (action == TradeAction.Buy)
            {
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        var obj = UnityEngine.Object.Instantiate(PrefabsDirectory.instance.GetGood(g).gameObject, m.transform.position + Vector3.up, CargoPlacement.Frame(m));
                        var good = obj.GetComponent<Good>(); staged.Add(good);
                        obj.GetComponent<ShipItem>().sold = true;
                        obj.GetComponent<SaveablePrefab>().RegisterToSave(); good.RegisterAsMissionless();
                    }
                    if (batch.Carrier) CartDelivery.Stage(batch, staged);
                    else if (!CargoPlacement.Place(m, staged, out int found))
                    {
                        // No room even turned: a bulk order is refused; a single unit drops the game's own way.
                        if (count > 1) throw new CargoPlacement.NoRoom(found, count);
                        CargoPlacement.Drop(m, staged[0]);
                    }
                }
                catch (Exception e)
                {
                    foreach (var good in staged) if (good) good.GetComponent<ShipItem>().DestroyItem();
                    Plugin.Log?.LogWarning(e); Notice.Show(e is CargoPlacement.NoRoom ? CargoPlacement.NoRoom.Notice : e.Message + "; no trade was charged."); return;
                }
            }
            int[] walletBefore = (int[])PlayerGold.currency.Clone();
            float stockBefore = m.currentSupply[g], tradedBefore = m.debugTotalGoodsTraded;
            Executing = true;
            try
            {
                if (batch.Carrier) PlayerGold.currency[batch.CartCurrency] = checked(PlayerGold.currency[batch.CartCurrency] - (int)batch.CartCharge);
                for (int i = 0; i < count; i++)
                {
                    var u = batch.Units[i];
                    if (action == TradeAction.Sell)
                    {
                        // As IslandMarketWarehouseArea.SellGood, for the chosen crate.
                        var item = u.Cargo.GetComponent<ShipItem>();
                        if (Fields.Get<List<Good>>(m.GetWarehouseArea(), "goodsInArea").Remove(u.Cargo))
                            m.currentPlayerGoods[g] = Math.Max(0, m.currentPlayerGoods[g] - 1);
                        if (batch.Carrier) CartDelivery.Detach(item);
                        item.DestroyItem(); m.SellGood(g); PlayerGold.currency[currency] = checked(PlayerGold.currency[currency] + u.Price);
                    }
                    else { PlayerGold.currency[currency] = checked(PlayerGold.currency[currency] - u.Price); m.PurchaseGood(g); }
                }
            }
            catch (Exception e)
            {
                if (action == TradeAction.Buy)
                {
                    Array.Copy(walletBefore, PlayerGold.currency, walletBefore.Length); m.currentSupply[g] = stockBefore; m.debugTotalGoodsTraded = tradedBefore;
                    m.UpdateSelfPriceReportForPlayer();
                    foreach (var good in staged) if (good) good.GetComponent<ShipItem>().DestroyItem();
                    Plugin.Log?.LogError(e); Notice.Show("Order cancelled; no trade was charged."); return;
                }
                throw;
            }
            finally { Executing = false; }
            // The receipt and the day log are presentation, after the whole commit.
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var u = batch.Units[i]; bool sale = action == TradeAction.Sell;
                    var item = (sale ? u.Cargo : staged[i]).GetComponent<ShipItem>();
                    if (DayLogs.instance) DayLogs.instance.dayLogs[currency].LogTransaction(sale ? u.Price : -u.Price, item);
                    if (EconomyUIReceiptScribe.instance) EconomyUIReceiptScribe.instance.AddTransaction(g, sale ? -1 : 1, u.Price, currency);
                }
            }
            catch (Exception e) { Plugin.Log?.LogError("Trade completed, but receipt display failed: " + e); }
            if (batch.Carrier)
            {
                if (action == TradeAction.Buy) GameState.loadedCargoIntoCart = true;
                if (DayLogs.instance) DayLogs.instance.dayLogs[batch.CartCurrency].LogTransaction(-(int)batch.CartCharge, TransactionCategory.other);
            }
            if (UISoundPlayer.instance) UISoundPlayer.instance.PlayGoldSound(); ui.RefreshPage();
        }
    }
    [HarmonyPatch(typeof(EconomyUI), "BuyGood")] internal static class BulkBuyPatch
    { static bool Prefix(EconomyUI __instance) { BulkTrade.Execute(__instance, TradeAction.Buy, BulkTrade.Quantity(Fields.Get<EconomyUIButton>(__instance, "buyButton"))); return false; } }
    [HarmonyPatch(typeof(EconomyUI), "SellGood")] internal static class BulkSellPatch
    { static bool Prefix(EconomyUI __instance) { BulkTrade.Execute(__instance, TradeAction.Sell, BulkTrade.Quantity(Fields.Get<EconomyUIButton>(__instance, "sellButton"))); return false; } }
    // The game's per-unit page refreshes wait while a batch runs; the batch refreshes once at its end.
    [HarmonyPatch(typeof(EconomyUI), "RefreshPage")] internal static class BatchedPagePatch { static bool Prefix() => !BulkTrade.Executing; }
    // The trade book (the clerk) opens from reputation 0: the game's single "level < 1" test becomes "level < 0".
    [HarmonyPatch(typeof(PortDude), "ActivateMissionListUI")] internal static class BookFromZeroPatch
    {
        internal static IEnumerable<CodeInstruction> Lower(IEnumerable<CodeInstruction> instructions)
        {
            var level = AccessTools.Method(typeof(PlayerReputation), "GetRepLevel", new[] { typeof(PortRegion) });
            var code = instructions.ToList(); int changed = 0;
            for (int i = 0; i + 1 < code.Count; i++)
                if (code[i].Calls(level) && code[i + 1].opcode == OpCodes.Ldc_I4_1) { code[i + 1].opcode = OpCodes.Ldc_I4_0; changed++; }
            if (changed != 1) throw new InvalidOperationException("Trade book reputation test changed (" + changed + " found)");
            return code;
        }
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => Lower(instructions);
    }

    [DefaultExecutionOrder(1000)]
    public sealed class BulkTradeUI : MonoBehaviour
    {
        private EconomyUI ui;
        private EconomyUIButton buy, sell;
        private float timer;
        private int previousKey = -1, previousHover = -1;
        private BookButton cart;
        private TextMesh cartMark, cartLabel, note;
        private GameObject cartRoot;
        private IslandMarket cartMarket;
        private bool cartSelected;
        private float buyTextSize, sellTextSize;
        private TextMesh storageLabel;
        private string storageLabelText;
        internal bool CartSelected => cartSelected && ui && ui.uiActive && !GameState.currentlyLoading && cartMarket == Fields.Get<IslandMarket>(ui, "currentIsland");
        internal static void Attach(EconomyUI ui)
        {
            if (ui.GetComponent<BulkTradeUI>()) return;
            try
            {
                var v = ui.gameObject.AddComponent<BulkTradeUI>();
                v.ui = ui; v.buy = Fields.Get<EconomyUIButton>(ui, "buyButton"); v.sell = Fields.Get<EconomyUIButton>(ui, "sellButton");
                v.buyTextSize = Fields.Get<TextMesh>(v.buy, "text").characterSize; v.sellTextSize = Fields.Get<TextMesh>(v.sell, "text").characterSize;
                v.storageLabel = ui.GetComponentsInChildren<TextMesh>(true).FirstOrDefault(t => t.text.Contains("storage area"));
                if (v.storageLabel) v.storageLabelText = v.storageLabel.text;
                v.CreateCartControl();
                foreach (var button in new[] { v.buy, v.sell })
                {
                    var label = Fields.Get<TextMesh>(button, "text");
                    if (label && !label.GetComponent<TextFit>())
                    {
                        var fit = label.gameObject.AddComponent<TextFit>(); fit.originalScale = label.transform.localScale;
                        // Keep the label within its plate.
                        var plate = button.GetComponent<MeshRenderer>(); fit.maxWidth = BookUI.InFrameBounds(plate, label.transform.parent).size.x * .92f;
                    }
                }
            }
            catch (Exception e) { BookLog.Error("building the bulk and cart controls; the book works without them", e); }
        }
        private void LateUpdate()
        {
            try { Tick(); } catch (Exception e) { BookLog.Error("updating the bulk and cart controls", e); }
        }
        private void Tick()
        {
            if (cartRoot) cartRoot.SetActive(ui && ui.uiActive);
            if (!ui || !ui.uiActive || GameState.currentlyLoading) { cartSelected = false; return; }
            int key = BulkTrade.HeldQuantity(), hover = ButtonHover.IsOver(buy) ? 1 : ButtonHover.IsOver(sell) ? 2 : 0;
            timer -= Time.unscaledDeltaTime;
            if (key != previousKey || hover != previousHover || timer <= 0) { previousKey = key; previousHover = hover; timer = .15f; Refresh(); }
        }
        internal void Refresh()
        {
            try { RefreshLabels(); } catch (Exception e) { BookLog.Error("refreshing the bulk labels", e); }
        }
        private void RefreshLabels()
        {
            if (!ui || !ui.uiActive || GameState.currentlyLoading || BulkTrade.Executing) return;
            var current = Fields.Get<IslandMarket>(ui, "currentIsland");
            if (cartMarket != current) { cartMarket = current; cartSelected = false; }
            var carrier = CartDelivery.Hired(current, out string cartError);
            // Receive a click even without a hire, so its reason can be shown; ToggleCart checks the hire again.
            cart.unclickable = false;
            cart.description = cartSelected ? "Use warehouse cargo for purchases and sales instead" : cartError ?? "Buy: load into your hired cart (10x normal transport fee). Sell: sell from that cart (20x normal transport fee plus storage). Cart fees require cash upfront.";
            cartMark.text = cartSelected ? "x" : "";
            if (current && CartSelected)
            {
                // The game writes the warehouse's count; with the cart chosen it is the cart's.
                var amounts = Fields.Get<TextMesh>(ui, "textGoodAmounts");
                var lines = amounts.text.Split('\n');
                amounts.text = lines[0] + "\n" + (carrier ? BulkTrade.SaleGoods(current, ui.currentSelectedGood, carrier).Count() : 0);
            }
            if (storageLabel) storageLabel.text = CartSelected ? storageLabelText.Replace("storage area", "hired cart") : storageLabelText;
            Apply(buy, TradeAction.Buy, BulkTrade.Quantity(buy)); Apply(sell, TradeAction.Sell, BulkTrade.Quantity(sell));
            if (current) note.text = BulkTrade.ConversionFee(current, (int)Fields.Get<Currency>(ui, "currentPlayerCurrency")) ? "Exchange fees included" : "";
        }
        internal void ResetCart() { cartSelected = false; cartMarket = ui ? Fields.Get<IslandMarket>(ui, "currentIsland") : null; }
        internal void ToggleCart()
        {
            if (!CartSelected && !CartDelivery.Hired(Fields.Get<IslandMarket>(ui, "currentIsland"), out string error)) { Notice.Show(error); Refresh(); return; }
            cartSelected = !CartSelected; ui.RefreshPage();
        }
        private void CreateCartControl()
        {
            var source = buy.GetComponent<MeshRenderer>(); var frame = source.transform.parent.parent.parent;
            cartRoot = new GameObject("Trade book cart delivery"); cartRoot.layer = source.gameObject.layer; cartRoot.transform.SetParent(frame, false);
            var font = Fields.Get<TextMesh>(ui, "textBuyPrice").font;
            // EconomyOverhaul's approved place: below the storage quantity, above Currency, left of Receipt.
            cartLabel = BookUI.Text(cartRoot.transform, font, "Load to/sell from cart", .311f, -.032f, .012f, .122f, TextAnchor.MiddleLeft);
            cartMark = BookUI.Text(cartRoot.transform, font, "", .299f, -.032f, .014f, .009f, TextAnchor.MiddleCenter);
            var box = new GameObject("Cart delivery checkbox"); box.layer = cartRoot.layer; box.transform.SetParent(cartRoot.transform, false);
            box.transform.localPosition = BookUI.Position(.299f, -.032f);
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            const float h = .0055f, t = .0006f;
            AddRect(vertices, triangles, -h, -h, h, -h + t); AddRect(vertices, triangles, -h, h - t, h, h);
            AddRect(vertices, triangles, -h, -h, -h + t, h); AddRect(vertices, triangles, h - t, -h, h, h);
            var mesh = new Mesh(); mesh.vertices = vertices.ToArray(); mesh.triangles = triangles.ToArray(); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            box.AddComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(source.sharedMaterial); material.color = Color.black; box.AddComponent<MeshRenderer>().sharedMaterial = material;
            cart = box.AddComponent<BookButton>(); cart.action = ToggleCart; cart.ownedMesh = mesh; cart.ownedMaterial = material;
            var hit = box.AddComponent<BoxCollider>(); hit.center = new Vector3(-.064f, 0, 0); hit.size = new Vector3(.142f, .008f, .019f);
            // The game's conversion note sits where the cart box goes: a compact one left of the box instead.
            var oldNote = Fields.Get<TextMesh>(ui, "textConversionInfo"); oldNote.gameObject.SetActive(false);
            note = BookUI.Text(cartRoot.transform, oldNote.font, "", .205f, -.032f, .011f, .083f, TextAnchor.MiddleLeft);
            note.fontStyle = FontStyle.Italic;
            cartRoot.SetActive(false);
        }
        private static void AddRect(List<Vector3> v, List<int> t, float x1, float z1, float x2, float z2)
        {
            int n = v.Count; v.Add(new Vector3(x1, 0, z1)); v.Add(new Vector3(x2, 0, z1)); v.Add(new Vector3(x2, 0, z2)); v.Add(new Vector3(x1, 0, z2));
            t.AddRange(new[] { n, n + 2, n + 1, n, n + 3, n + 2 });
        }
        private void Apply(EconomyUIButton button, TradeAction action, int count)
        {
            var q = BulkTrade.Quote(ui, action, count); button.unclickable = !q.Valid; button.description = q.Breakdown();
            string name = action.ToString() + (count > 1 ? " " + count : "");
            string label = !q.Valid ? name + " unavailable" : name + " (" + (action == TradeAction.Sell ? q.NetPayout : q.Total).ToString("N0") + ")";
            if (q.Valid && q.Carrier) label += "\nCart: " + q.CartCharge.ToString("N0") + " " + PlayerGold.GetCurrencyName(q.CartCurrency);
            button.SetButtonText(label);
            Fields.Get<TextMesh>(button, "text").characterSize = (button == buy ? buyTextSize : sellTextSize) * (label.Contains("\n") ? .78f : 1);
            button.SetButtonMaterial(Fields.Get<Material[]>(ui, "buttonMaterials")[q.Valid ? 0 : 1]);
        }
    }
    [HarmonyPatch(typeof(EconomyUI), "RefreshPage")] internal static class BulkRefreshPatch
    { static void Postfix(EconomyUI __instance) { var v = __instance.GetComponent<BulkTradeUI>(); if (v) v.Refresh(); } }
    [HarmonyPatch(typeof(EconomyUI), "OpenUI")] internal static class CartSessionPatch
    {
        static void Prefix(EconomyUI __instance) { BulkTradeUI.Attach(__instance); var v = __instance.GetComponent<BulkTradeUI>(); if (v) v.ResetCart(); }
    }
    // A clickable part of the book the mod adds (EconomyOverhaul's LoanAction).
    public sealed class BookButton : GoPointerButton
    {
        internal Action action;
        internal Material ownedMaterial;
        internal Mesh ownedMesh;
        public override void OnActivate() { if (!unclickable) action?.Invoke(); }
        private void OnDestroy() { if (ownedMaterial) Destroy(ownedMaterial); if (ownedMesh) Destroy(ownedMesh); }
    }
    // The book's text helpers (EconomyOverhaul's BookUI, the parts the cart box uses).
    internal static class BookUI
    {
        private const float TextScale = .8f;
        internal static Vector3 Position(float x, float y, float depth = .022f) => new Vector3(-x, depth, -y);
        internal static TextMesh Text(Transform parent, Font font, string text, float x, float y, float size, float width = 0, TextAnchor anchor = TextAnchor.LowerLeft)
        {
            var go = new GameObject("Better Trade Book text"); go.layer = parent.gameObject.layer; go.transform.SetParent(parent, false);
            go.transform.localPosition = Position(x, y);
            go.transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.back);
            go.transform.localScale = Vector3.one * (size * TextScale / 6.4f);
            var t = go.AddComponent<TextMesh>(); t.font = font; t.fontSize = 64; t.characterSize = 1; t.anchor = anchor;
            t.alignment = anchor == TextAnchor.LowerRight ? TextAlignment.Right : anchor == TextAnchor.MiddleCenter ? TextAlignment.Center : TextAlignment.Left;
            t.color = Color.black; t.richText = false; t.lineSpacing = .58f; t.text = text;
            go.GetComponent<MeshRenderer>().sharedMaterial = t.font.material;
            if (width > 0) { var fit = go.AddComponent<TextFit>(); fit.maxWidth = width; fit.originalScale = go.transform.localScale; }
            return t;
        }
        internal static Bounds InFrameBounds(MeshRenderer renderer, Transform frame)
        {
            var raw = renderer.GetComponent<MeshFilter>().sharedMesh.bounds;
            var bounds = new Bounds(frame.InverseTransformPoint(renderer.transform.TransformPoint(raw.center)), Vector3.zero);
            for (int i = 0; i < 8; i++)
                bounds.Encapsulate(frame.InverseTransformPoint(renderer.transform.TransformPoint(raw.center + Vector3.Scale(raw.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)))));
            return bounds;
        }
    }
    // Shrinks a text to its width (EconomyOverhaul's LoanTextFit).
    public sealed class TextFit : MonoBehaviour
    {
        internal float maxWidth; internal Vector3 originalScale;
        private string last; private TextMesh text;
        private void LateUpdate()
        {
            if (!text) text = GetComponent<TextMesh>();
            if (last == text.text) return; last = text.text;
            transform.localScale = originalScale;
            text.font.RequestCharactersInTexture(text.text, text.fontSize, text.fontStyle);
            float widest = 0, current = 0;
            foreach (char c in text.text)
            {
                if (c == '\n') { widest = Mathf.Max(widest, current); current = 0; }
                else if (text.font.GetCharacterInfo(c, out var info, text.fontSize, text.fontStyle)) current += info.advance * .1f * text.characterSize * originalScale.x;
            }
            widest = Mathf.Max(widest, current);
            if (widest > maxWidth) transform.localScale = originalScale * (maxWidth / widest);
        }
    }
    // ---- Copied unchanged from EconomyOverhaul's NewMarket.cs (only its two water-cover rescans left out) ----
    // The trade book uses Ctrl for x10 (HeldQuantity), and vanilla toggles crouch on the crouch key whenever the
    // game is playing, so Ctrl in the open book crouched the player. The key press is ignored while the book is open.
    // GameInput and InputName live in the game's Oculus.VR assembly, so the key check is found by name and
    // filtered right after it instead of being replaced.
    [HarmonyPatch(typeof(PlayerCrouching),"Update")] internal static class BookCrouchPatch
    {
        internal static bool NotInBook(bool pressed) => pressed && !(EconomyUI.instance && EconomyUI.instance.uiActive);
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var keyDown = AccessTools.Method(AccessTools.TypeByName("GameInput"), "GetKeyDown");
            var list = instructions.ToList();
            int at = keyDown == null ? -1 : list.FindIndex(c => c.Calls(keyDown));
            if (at < 0 || list.Count(c => c.Calls(keyDown)) != 1) { Plugin.Log?.LogWarning("Crouch key check not found; Ctrl in the trade book may still crouch."); return list; }
            list.Insert(at + 1, new CodeInstruction(System.Reflection.Emit.OpCodes.Call, AccessTools.Method(typeof(BookCrouchPatch), nameof(NotInBook))));
            return list;
        }
    }

    // Native IsLookedAt stops being refreshed when a button is unclickable.
    // Eligibility must not change which batch the pointer is asking for.
    internal static class ButtonHover
    {
        internal static bool IsOver(GoPointerButton button)
        {
            if (!button || !button.gameObject.activeInHierarchy) return false;
            if (!GameState.inCursorMenu) return button.IsLookedAt();
            var pointer = MouseButtonPointer.instance;
            if (!pointer) return false;
            var uiCamera = Fields.Get<Camera>(pointer, "uiCam");
            var worldCamera = Camera.main;
            if (!uiCamera || !worldCamera) return false;
            bool mouse = Fields.Get<bool>(pointer, "trackMouse");
            Vector3 uiScreen = mouse ? Input.mousePosition : uiCamera.WorldToScreenPoint(pointer.controllerPointer.position);
            Vector3 worldScreen = mouse ? Input.mousePosition : worldCamera.WorldToScreenPoint(pointer.controllerPointer.position);
            RaycastHit hit;
            if (!Physics.Raycast(uiCamera.ScreenPointToRay(uiScreen), out hit, 160f, 32) &&
                !Physics.Raycast(worldCamera.ScreenPointToRay(worldScreen), out hit, 160f, 8388608)) return false;
            return hit.collider.GetComponent<GoPointerButton>() == button;
        }
    }

    // Uses native carrier ownership/save slots. The higher fee applies only to
    // trade-book delivery; normal cart loading and later storage are unchanged.
    internal static class CartDelivery
    {
        internal static CargoCarrier Hired(IslandMarket market, out string error)
        {
            error = "Hire this port's cargo transport first";
            var storage = CargoStorageUI.instance;
            if (!market || !storage) return null;
            var carrier = Fields.Get<CargoCarrier>(storage, "currentCarrier");
            var dude = Fields.Get<Transform>(storage, "currentDude");
            var port = market.GetComponent<Port>();
            var island = port && port.island ? port.island.GetComponentInParent<IslandHorizon>() : market.GetComponentInParent<IslandHorizon>();
            if (!island) { error = "Cargo transport unavailable at this port"; return null; }
            // Vanilla carrier slots use island IDs, not market IDs (Neverdin 3 vs 2).
            // Where a streamed NPC is available, validate its explicit registration too.
            var npc = dude ? dude.GetComponent<CargoTransportDude>() : null;
            var scenery = dude ? dude.GetComponentInParent<IslandSceneryScene>() : null;
            int service = scenery && scenery.parentIslandIndex == island.islandIndex && npc ? npc.carrierIndex : island.islandIndex;
            if (!carrier || CargoCarrier.carriers == null || service < 0 || service >= CargoCarrier.carriers.Length ||
                carrier != CargoCarrier.carriers[service] || carrier.portIndex != service || !dude ||
                (scenery && scenery.parentIslandIndex != island.islandIndex) || (npc && npc.carrierIndex != service) ||
                !Refs.observerMirror || Vector3.Distance(Refs.observerMirror.transform.position, dude.position) > 180) return null;
            error = null; return carrier;
        }
        internal static int Fee(CargoCarrier carrier, ShipItem item, int multiplier = 10)
        {
            int normal = carrier.GetTransportPrice(item);
            if (normal < 0) throw new OverflowException("Cart fee exceeds wallet limit");
            return checked(normal * multiplier);
        }
        internal static void Stage(TradeBatch batch, List<Good> goods)
        {
            var carrier = Hired(batch.Market, out string error);
            if (!carrier || carrier != batch.Carrier) throw new InvalidOperationException(error ?? "Cart service changed");
            foreach (var good in goods)
            {
                var item = good.GetComponent<ShipItem>();
                if (!item.GetItemRigidbody() || !item.GetComponent<Collider>() || item is ShipItemCrate && item.amount <= 0 || Fee(carrier,item) != batch.CartUnitFee)
                    throw new InvalidOperationException("Cargo cannot be loaded into the cart");
            }
            // The adapter performs the native attachment steps without native
            // per-item charging. Aggregate cash is validated by the batch quote.
            foreach (var good in goods)
            {
                var item = good.GetComponent<ShipItem>();
                item.GetItemRigidbody().EnterInventorySlot(carrier.transform);
                item.InsertIntoCargoCarrier(carrier);
                item.transform.localScale = Vector3.zero;
                item.daysInStorage = 0;
                carrier.cargo.Add(item);
            }
        }
        internal static void Detach(ShipItem item)
        {
            if (CargoCarrier.carriers == null) return;
            bool removed=false;
            foreach (var carrier in CargoCarrier.carriers) if (carrier) removed |= carrier.cargo.Remove(item);
            // Native EnterInventorySlot starts a next-frame layer update on the
            // separate rigidbody object. Destruction otherwise leaves that update
            // racing ShipItem.OnDestroy, notably during a rolled-back order.
            if(removed && item.GetItemRigidbody())item.GetItemRigidbody().StopAllCoroutines();
        }
    }

    internal static class CargoPlacement
    {
        internal const float Gap = .03f;
        internal static Bounds Footprint(Good good, Quaternion rotation)
        {
            bool initialized=false;var bounds=new Bounds();var inverse=Quaternion.Inverse(rotation);
            foreach(var collider in good.GetComponentsInChildren<Collider>()){
                Bounds local;
                if(collider is BoxCollider box)local=new Bounds(box.center,box.size);
                else if(collider is MeshCollider mesh && mesh.sharedMesh)local=mesh.sharedMesh.bounds;
                else if(collider is SphereCollider sphere)local=new Bounds(sphere.center,Vector3.one*sphere.radius*2);
                else if(collider is CapsuleCollider capsule){var size=Vector3.one*capsule.radius*2;size[capsule.direction]=Mathf.Max(capsule.height,size[capsule.direction]);local=new Bounds(capsule.center,size);}
                else throw new InvalidOperationException("Unsupported cargo shape");
                for(int i=0;i<8;i++){
                    var p=local.center+Vector3.Scale(local.extents,Corner(i));
                    p=inverse*(collider.transform.TransformPoint(p)-good.transform.position);
                    if(!initialized){bounds=new Bounds(p,Vector3.zero);initialized=true;}else bounds.Encapsulate(p);
                }
            }
            if(!initialized)throw new InvalidOperationException("Cargo shape unavailable");
            return bounds;
        }
        private static Vector3 Corner(int i)=>new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1);
        internal static bool Inside(BoxCollider area,Vector3 center,Vector3 half,Quaternion rotation)
        {
            for(int i=0;i<8;i++){
                Vector3 p=area.transform.InverseTransformPoint(center+rotation*Vector3.Scale(half,Corner(i)))-area.center;
                if(Mathf.Abs(p.x)>area.size.x*.5f || Mathf.Abs(p.y)>area.size.y*.5f || Mathf.Abs(p.z)>area.size.z*.5f)return false;
            }
            return true;
        }
        private static bool Cargo(Collider c)=>c.GetComponentInParent<ShipItem>() || c.GetComponentInParent<ItemRigidbody>();
        private sealed class Candidate
        {
            internal Vector3 Center;
            internal bool ReservedSupport, Stack;
        }
        // How far a crate's bottom would sit above the ground (not cargo) under its centre.
        private static float AboveGround(Vector3 center, float halfHeight, HashSet<Collider> own)
        {
            var bottom = center - Vector3.up * (halfHeight + Gap);
            foreach (var hit in Physics.RaycastAll(bottom + Vector3.up * .01f, Vector3.down, 30, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
                if (!own.Contains(hit.collider) && !Cargo(hit.collider)) return bottom.y - hit.point.y;
            return float.PositiveInfinity;
        }
        private static bool Ignored(Collider c, HashSet<Collider> own) => own.Contains(c) || (c.isTrigger && !Cargo(c));
        private static bool StableCargo(Collider c)
        {
            if (!Cargo(c)) return false;
            var item = c.GetComponentInParent<ShipItem>();
            var rigid = item ? item.GetItemRigidbody() : c.GetComponentInParent<ItemRigidbody>();
            if (!item && rigid) item = rigid.GetShipItem();
            if (item && (item.held || item.GetCurrentInventorySlot() >= 100)) return false;
            var body = rigid ? rigid.GetBody() : c.attachedRigidbody;
            return !body || body.isKinematic || (body.velocity.sqrMagnitude < .04f && body.angularVelocity.sqrMagnitude < .04f);
        }
        private static bool Supported(Vector3 center, Vector3 half, Quaternion rotation, HashSet<Collider> own)
        {
            float top = center.y - half.y - Gap;
            // Center and all four corners must rest on the same local height. Each
            // cell has its own support height; no shared pickup-floor/roof constraint.
            for (int i = 0; i < 5; i++) {
                var offset = i == 4 ? Vector3.zero : new Vector3((i & 1) == 0 ? -half.x + .005f : half.x - .005f, 0,
                    (i & 2) == 0 ? -half.z + .005f : half.z - .005f);
                var start = center + rotation * offset; start.y = top + .06f;
                var hits = Physics.RaycastAll(start, Vector3.down, .12f, ~0, QueryTriggerInteraction.Collide)
                    .Where(h => !Ignored(h.collider, own)).OrderBy(h => h.distance).ToArray();
                if (hits.Length == 0) return false;
                var hit = hits[0];
                if (hit.normal.y < .98f || Mathf.Abs(hit.point.y - top) > .025f ||
                    (Cargo(hit.collider) ? !StableCargo(hit.collider) : hit.collider.attachedRigidbody != null)) return false;
            }
            return true;
        }
        // Uneven floors and slopes (the user, 2026-10-08: placed cargo must never slide or move). Where the level test above fails,
        // the crate rests on the plane through the highest ground under it (an upper face of the ground points' hull with its
        // centre inside), tilted to that plane, if the tilt is within its shape's limit. Tested in game (-SlopeProbe, 10 s of the
        // game's physics): nothing slid below 30°; tall thin cargo tips first, at atan(thickness / height) (rabbit furs 16°,
        // lumber 8°). The limit keeps TipMargin under that, and under MaxTilt (3°, see RoughPorts).
        // Only at the ports whose warehouses are rough ground (the user, 2026-10-08): Al'Nilem (1) and Mirage Mountain (32) (Dragon Cliffs tried: it lost capacity). Measured
        // with the game's physics (-WarehouseSurvey, 10 s after a full warehouse): there nothing moved at 3°, a 5 cm dip,
        // stacked only on level crates (40 and 20 standard crates; 29 and 12 before). MaxStack (no limit) is for measuring.
        internal static readonly HashSet<int> RoughPorts = new HashSet<int> { 1, 32 };
        internal static float MaxTilt = 3f, TipMargin = 4f, MaxStack = float.PositiveInfinity, MaxDip = .05f;
        private static float FloorNormal => Mathf.Cos(MaxTilt * Mathf.Deg2Rad);
        private const float LevelTop = .99995f;   // cos 0.57°
        internal static float TiltLimit(Vector3 size) => Mathf.Min(MaxTilt, Mathf.Atan2(Mathf.Min(size.x, size.z), size.y) * Mathf.Rad2Deg - TipMargin);
        // level: the crate stands on a level floor (the test above); only such a crate takes another on top.
        private static bool Rest(ref Vector3 center, ref Quaternion rotation, Vector3 half, HashSet<Collider> own, bool rough, out bool level)
        {
            level = Supported(center, half, rotation, own); if (level || !rough) return level;
            float limit = TiltLimit(half * 2); if (limit <= 0) return false;
            float floorY = center.y - half.y - Gap, rise = .1f + .55f * Mathf.Max(half.x, half.z);
            int nx = half.x > 1.2f ? 5 : 4, nz = half.z > 1.2f ? 5 : 4;
            var points = new List<Vector3>();
            for (int ix = 0; ix < nx; ix++) for (int iz = 0; iz < nz; iz++)
            {
                var local = new Vector3(Mathf.Lerp(-half.x + .02f, half.x - .02f, ix / (nx - 1f)), 0, Mathf.Lerp(-half.z + .02f, half.z - .02f, iz / (nz - 1f)));
                var world = center + rotation * local; var start = new Vector3(world.x, floorY + rise, world.z);
                var hit = Physics.RaycastAll(start, Vector3.down, rise * 2, ~0, QueryTriggerInteraction.Collide).Where(h => !Ignored(h.collider, own)).OrderBy(h => h.distance).Select(h => (RaycastHit?)h).FirstOrDefault();
                if (!hit.HasValue) return false;   // a hole or an overhang under the crate
                var c = hit.Value.collider;
                if (Cargo(c) ? !StableCargo(c) : c.attachedRigidbody != null) return false;
                points.Add(new Vector3(local.x, hit.Value.point.y, local.z));
            }
            if (points.Count < 3) return false;
            // The face the crate's centre (0, 0) rests on: three points around the centre (not near an edge), all others below.
            for (int i = 0; i < points.Count; i++) for (int j = i + 1; j < points.Count; j++) for (int k = j + 1; k < points.Count; k++)
            {
                Vector3 a = points[i], b = points[j], c = points[k];
                var n = Vector3.Cross(b - a, c - a); if (Mathf.Abs(n.y) < 1e-5f) continue; if (n.y < 0) n = -n;
                float area2 = (b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z);
                if (Mathf.Abs(area2) < 1e-6f) continue;
                float u = (b.x * c.z - c.x * b.z) / area2, v = (c.x * a.z - a.x * c.z) / area2, w = 1 - u - v;   // barycentric of (0, 0)
                if (u < .1f || v < .1f || w < .1f) continue;
                n.Normalize(); bool below = true;
                foreach (var q in points) { float off = Vector3.Dot(q - a, n); if (off > .002f || off < -MaxDip) { below = false; break; } }
                if (!below) continue;
                // n is in the crate's level frame (x, z level; y up): its tilt, and the plane's height under the centre.
                if (Vector3.Angle(n, Vector3.up) > limit) return false;
                float y0 = a.y - (n.x * (0 - a.x) + n.z * (0 - a.z)) / n.y;
                var normal = (rotation * new Vector3(n.x, 0, n.z) + Vector3.up * n.y).normalized;
                rotation = Quaternion.FromToRotation(Vector3.up, normal) * rotation;
                center = new Vector3(center.x, y0, center.z) + normal * (Gap + half.y);
                return true;
            }
            return false;
        }
        // A crate's box, turned its own way, measured along the order's frame (level crates: exactly half its size).
        private static Vector3 Extent(Quaternion rotation, Quaternion frame, Vector3 size)
        {
            var r = Quaternion.Inverse(frame) * rotation; Vector3 e = Vector3.zero;
            for (int k = 0; k < 3; k++) { var axis = r * new Vector3(k == 0 ? 1 : 0, k == 1 ? 1 : 0, k == 2 ? 1 : 0) * size[k] * .5f; e += new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z)); }
            return e;
        }
        // Follow connected paving from a known low floor, not a ray cast from above
        // the building. Cargo is never a floor-map surface (stacking is separate).
        private sealed class FloorMap
        {
            private const float Step = .35f, Rise = 1.2f;
            private readonly float cell, baseHeight;
            private readonly Vector3 origin, min;
            private readonly Quaternion rotation;
            private readonly float[,] heights;
            private readonly HashSet<Collider> own;
            internal FloorMap(Vector3 origin, Quaternion rotation, Vector3 min, Vector3 max,
                Vector3 seed, HashSet<Collider> own)
            {
                this.origin=origin;this.rotation=rotation;this.min=min;this.own=own;baseHeight=seed.y;
                cell=Mathf.Max(.25f,Mathf.Max(max.x-min.x,max.z-min.z)/160);
                int nx=Mathf.CeilToInt((max.x-min.x)/cell)+1,nz=Mathf.CeilToInt((max.z-min.z)/cell)+1;
                heights=new float[nx,nz];
                for(int x=0;x<nx;x++)for(int z=0;z<nz;z++)heights[x,z]=float.NaN;
                var queue=new Queue<Vector2Int>();
                // Seed low, unobstructed surfaces at the established ground level.
                // Multiple seeds keep pillars/cargo from cutting off separate floor patches.
                for(int x=0;x<nx;x++)for(int z=0;z<nz;z++)
                    if(Surface(Point(x,z),baseHeight,out float start)){heights[x,z]=start;queue.Enqueue(new Vector2Int(x,z));}
                int[] dx={-1,1,0,0},dz={0,0,-1,1};
                while(queue.Count>0){var p=queue.Dequeue();for(int i=0;i<4;i++){
                    int x=p.x+dx[i],z=p.y+dz[i];
                    if(x<0||z<0||x>=nx||z>=nz)continue;
                    // Upgrade a buried low hit when a neighbouring step reaches the
                    // real paving above it. Heights only increase, bounding revisits.
                    if(Surface(Point(x,z),heights[p.x,p.y],out float h)&&
                        (float.IsNaN(heights[x,z])||h>heights[x,z]+.01f)){heights[x,z]=h;queue.Enqueue(new Vector2Int(x,z));}
                }}
            }
            private Vector3 Point(int x,int z)=>origin+rotation*new Vector3(min.x+x*cell,0,min.z+z*cell);
            private bool Surface(Vector3 point,float near,out float height)
            {
                point.y=near+Step+.01f;height=0;
                foreach(var hit in Physics.RaycastAll(point,Vector3.down,Step*2+.02f,~0,QueryTriggerInteraction.Ignore).OrderBy(h=>h.distance)){
                    if(Ignored(hit.collider,own)||Cargo(hit.collider)||hit.collider.attachedRigidbody||hit.normal.y<.98f||
                        hit.point.y>baseHeight+Rise||Mathf.Abs(hit.point.y-near)>Step)continue;
                    height=hit.point.y;return true;
                }
                return false;
            }
            internal bool Height(Vector3 point,out float height)
            {
                var local=Quaternion.Inverse(rotation)*(point-origin);
                int x=Mathf.Clamp(Mathf.RoundToInt((local.x-min.x)/cell),0,heights.GetLength(0)-1),z=Mathf.Clamp(Mathf.RoundToInt((local.z-min.z)/cell),0,heights.GetLength(1)-1);
                height=0;return !float.IsNaN(heights[x,z])&&Surface(point,heights[x,z],out height);
            }
        }
        // The placement's upright turn. A market standing upright (every port but Serpent Isle) keeps its own turn exactly;
        // Serpent Isle's market (and its warehouse box) is tipped 90° on its side: its most level axis becomes "forward".
        internal static Quaternion Frame(IslandMarket market)
        {
            var t = market.transform;
            if (Vector3.Dot(t.up, Vector3.up) > .9999f) return t.rotation;
            var forward = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (forward.sqrMagnitude < .25f) forward = Vector3.ProjectOnPlane(t.up, Vector3.up);
            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }
        // An order that fits nowhere, even turned. The log keeps the count; the player sees the notice.
        internal sealed class NoRoom : InvalidOperationException
        {
            internal const string Notice = "Not enough space for Bulk Trading. Please buy one by one";
            internal NoRoom(int found, int count) : base("Found space for " + found + " of " + count + " cargo; full order needs more clear warehouse space") { }
        }
        // The whole order as the market faces, else turned a quarter turn one way, else the other (the user, 2026-10-08). At the rough
        // ports (RoughPorts) crate by crate instead: as many as fit as faced, then the rest turned, until a round places none.
        internal static bool Place(IslandMarket market, List<Good> goods, out int found)
        {
            var frame = Frame(market); var left = new List<Good>(goods); found = 0; bool progress = true;
            var turns = new[] { frame, frame * Quaternion.Euler(0, 90, 0), frame * Quaternion.Euler(0, -90, 0) };
            bool rough = RoughPorts.Contains(market.GetPortIndex());
            if (!rough)
            {
                foreach (var turn in turns)
                {
                    foreach (var good in goods) good.transform.rotation = turn;
                    int placed = Plan(market, goods, turn, false, false);
                    if (placed == goods.Count) { found = placed; return true; }
                    found = Math.Max(found, placed);
                }
                return false;
            }
            while (left.Count > 0 && progress)
            {
                progress = false;
                foreach (var turn in turns)
                {
                    if (left.Count == 0) break;
                    foreach (var good in left) good.transform.rotation = turn;
                    int placed = Plan(market, left, turn, rough, true);
                    if (placed > 0) { found += placed; left.RemoveRange(0, placed); progress = true; }
                }
            }
            return left.Count == 0;
        }
        // The game's own way (IslandMarket.SpawnGood): 1 m above the market, left to physics; upright.
        internal static void Drop(IslandMarket market, Good good)
        {
            good.transform.SetPositionAndRotation(market.transform.position + Vector3.up, Frame(market));
            Physics.SyncTransforms();
        }
        // Places as many of the goods (in order) as fit with this turn; returns how many.
        // rough: the rough-ground rules above; partial: place what fits (else only a full order is placed).
        private static int Plan(IslandMarket market, List<Good> goods, Quaternion rotation, bool rough, bool partial)
        {
            float floorNormal = rough ? FloorNormal : .98f, stackTop = rough ? LevelTop : .98f, maxStack = rough ? MaxStack : float.PositiveInfinity;
            var area = market.GetWarehouseArea().GetComponent<BoxCollider>();
            if (!area) throw new InvalidOperationException("Warehouse placement area unavailable");
            Physics.SyncTransforms();
            var own = new HashSet<Collider>(goods.SelectMany(g => g.GetComponentsInChildren<Collider>()));
            var origin = market.transform.position;
            // No warehouse floor under the sea (beside Serpent Isle's docks the box takes in the sea bed).
            float sea = Crest.OceanRenderer.Instance ? Crest.OceanRenderer.Instance.transform.position.y : float.NegativeInfinity;
            var bounds = Footprint(goods[0], rotation); var half = bounds.extents;
            var inverse = Quaternion.Inverse(rotation);
            var min = Vector3.one * float.PositiveInfinity; var max = Vector3.one * float.NegativeInfinity;
            for (int i = 0; i < 8; i++) {
                var p = inverse * (area.transform.TransformPoint(area.center + Vector3.Scale(area.size * .5f, Corner(i))) - origin);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
            float width = Mathf.Max(.1f, bounds.size.x + Gap), depth = Mathf.Max(.1f, bounds.size.z + Gap);
            int x0 = Mathf.CeilToInt((min.x + half.x) / width), x1 = Mathf.FloorToInt((max.x - half.x) / width);
            int z0 = Mathf.CeilToInt((min.z + half.z) / depth), z1 = Mathf.FloorToInt((max.z - half.z) / depth);
            var floor = new List<Candidate>(); var stacks = new List<Candidate>();
            for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++) {
                var p = origin + rotation * new Vector3(x * width, 0, z * depth);
                // Native pickup height prevents treating a building roof as floor.
                var start = new Vector3(p.x, origin.y + .1f, p.z);
                float distance = Mathf.Max(4, start.y - area.bounds.min.y + 1);
                foreach (var hit in Physics.RaycastAll(start, Vector3.down, distance, ~0, QueryTriggerInteraction.Collide).OrderBy(h => h.distance)) {
                    if (Ignored(hit.collider, own) || Cargo(hit.collider) || hit.collider.attachedRigidbody || hit.normal.y < floorNormal) continue;
                    floor.Add(new Candidate { Center = new Vector3(p.x, hit.point.y + Gap + half.y, p.z) }); break;
                }
                start.y = area.bounds.max.y + .1f;
                foreach (var hit in Physics.RaycastAll(start, Vector3.down, area.bounds.size.y + .2f, ~0, QueryTriggerInteraction.Collide)) {
                    if (!Ignored(hit.collider, own) && StableCargo(hit.collider) && hit.normal.y > stackTop)
                        stacks.Add(new Candidate { Center = new Vector3(p.x, hit.point.y + Gap + half.y, p.z), Stack = true });
                }
            }
            floor = floor.OrderBy(c => (c.Center - origin).sqrMagnitude).ToList();
            var positions = new List<Vector3>(); var turns = new List<Quaternion>();
            Vector3? seed=floor.Count>0?(Vector3?)(floor[0].Center-Vector3.up*(Gap+half.y)):null;
            if(!seed.HasValue)foreach(var hit in Physics.RaycastAll(origin+Vector3.up*.1f,Vector3.down,Mathf.Max(4,origin.y-area.bounds.min.y+1),~0,QueryTriggerInteraction.Ignore).OrderBy(h=>h.distance))
                if(!Ignored(hit.collider,own)&&!Cargo(hit.collider)&&!hit.collider.attachedRigidbody&&hit.normal.y>=floorNormal){seed=hit.point;break;}
            bool searchedGaps=false,stacksSorted=false;
            int floorIndex=0;

            // Reserve the full batch before moving goods or touching the transaction.
            while (floorIndex < floor.Count || stacks.Count > 0 || !searchedGaps) {
                if(floorIndex==floor.Count&&!searchedGaps){
                    searchedGaps=true;
                    floor.Clear();floorIndex=0;
                    // Quarter-cell offsets also cover narrow boundary strips excluded by
                    // the pickup-anchored coarse grid. Only pay this cost when necessary.
                    var map=seed.HasValue?new FloorMap(origin,rotation,min,max,seed.Value,own):null;
                    float left=min.x+half.x+.006f,right=max.x-half.x-.006f;
                    float back=min.z+half.z+.006f,front=max.z-half.z-.006f;
                    int nx=Mathf.Max(1,Mathf.CeilToInt((right-left)/(width*.25f))),nz=Mathf.Max(1,Mathf.CeilToInt((front-back)/(depth*.25f)));
                    for(int z=0;z<=nz&&front>=back;z++)for(int x=0;x<=nx&&right>=left;x++){
                        var p=origin+rotation*new Vector3(Mathf.Lerp(left,right,(float)x/nx),0,Mathf.Lerp(back,front,(float)z/nz));
                        float h=0;bool found=map!=null&&map.Height(p,out h);
                        // Fine offsets must retain every floor accepted by the original
                        // pickup-height probe, even across a step larger than the map limit.
                        var lowStart=new Vector3(p.x,origin.y+.1f,p.z);
                        foreach(var hit in Physics.RaycastAll(lowStart,Vector3.down,Mathf.Max(4,lowStart.y-area.bounds.min.y+1),~0,QueryTriggerInteraction.Collide).OrderBy(hit=>hit.distance)){
                            if(Ignored(hit.collider,own)||Cargo(hit.collider)||hit.collider.attachedRigidbody||hit.normal.y<floorNormal)continue;
                            if(!found||hit.point.y>h)h=hit.point.y;found=true;break;
                        }
                        if(found)floor.Add(new Candidate{Center=new Vector3(p.x,h+Gap+half.y,p.z)});
                        // Existing stacks can also be offset from the original grid.
                        var start=new Vector3(p.x,area.bounds.max.y+.1f,p.z);
                        foreach(var hit in Physics.RaycastAll(start,Vector3.down,area.bounds.size.y+.2f,~0,QueryTriggerInteraction.Collide))
                            if(!Ignored(hit.collider,own)&&StableCargo(hit.collider)&&hit.normal.y>stackTop)
                                stacks.Add(new Candidate{Center=new Vector3(p.x,hit.point.y+Gap+half.y,p.z),Stack=true});
                    }
                    floor=floor.OrderBy(c=>(c.Center-origin).sqrMagnitude).ToList();
                    if(floor.Count==0&&stacks.Count==0)break;
                }
                Candidate candidate;
                if (floorIndex < floor.Count) { candidate = floor[floorIndex++]; }
                else {
                    if(!stacksSorted){stacks=stacks.OrderByDescending(c=>c.Center.y).ThenByDescending(c=>(c.Center-origin).sqrMagnitude).ToList();stacksSorted=true;}
                    candidate=stacks[stacks.Count-1];stacks.RemoveAt(stacks.Count-1);
                }
                var center = candidate.Center; var turn = rotation;
                if (candidate.Stack && maxStack < float.PositiveInfinity && AboveGround(center, half.y, own) > maxStack) continue;
                bool level = true;
                if (!candidate.ReservedSupport && !Rest(ref center, ref turn, half, own, rough, out level)) continue;
                var extent = Extent(turn, rotation, bounds.size);
                if (center.y - extent.y - Gap < sea) continue;
                if (!Inside(area, center, half + Vector3.one * .005f, turn)) continue;
                bool fits = true;
                for (int i = 0; i < positions.Count; i++) {
                    var d = inverse * (center - positions[i]); var e = extent + Extent(turns[i], rotation, bounds.size);
                    if (Mathf.Abs(d.x) < e.x + Gap-.0001f && Mathf.Abs(d.y) < e.y + Gap-.0001f && Mathf.Abs(d.z) < e.z + Gap-.0001f) { fits = false; break; }
                }
                if (!fits) continue;
                // Keep vertical tolerance below the drop gap: distant ports lose
                // millimetres of precision in world-space floor ray hits.
                foreach (var hit in Physics.OverlapBox(center, half + new Vector3(Gap-.005f,.005f,Gap-.005f), turn, ~0, QueryTriggerInteraction.Collide)) {
                    if (own.Contains(hit) || hit == area || hit.isTrigger && !Cargo(hit)) continue;
                    fits = false; break;
                }
                if (!fits) continue;
                positions.Add(center); turns.Add(turn);
                if (positions.Count == goods.Count) break;
                // Only a level crate takes another on top before it is placed.
                if (level) { stacks.Add(new Candidate { Center = center + Vector3.up * (bounds.size.y + Gap), ReservedSupport = true, Stack = true }); stacksSorted=false; }
            }
            if (!partial && positions.Count < goods.Count) return positions.Count;
            for (int i = 0; i < positions.Count; i++) goods[i].transform.SetPositionAndRotation(positions[i] - turns[i] * bounds.center, turns[i]);
            Physics.SyncTransforms();
            return positions.Count;
        }
    }
}
