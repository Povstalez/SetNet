# SetNet — детальний посібник користувача

Повна інструкція з використання бібліотеки: від «hello world» до production-конфігурації.
Короткий огляд — у [README](../README.md); продуктивність і межі масштабування — у [PERFORMANCE.ua.md](PERFORMANCE.ua.md).

## Зміст
1. [Вимоги та встановлення](#1-вимоги-та-встановлення)
2. [Базові концепти](#2-базові-концепти)
3. [Швидкий старт](#3-швидкий-старт)
4. [Повідомлення та хендлери](#4-повідомлення-та-хендлери)
5. [Уніфікований протокол: запит/відповідь, push, RPC](#5-уніфікований-протокол-запитвідповідь-push-rpc)
6. [Транспорти: TCP / UDP / Both](#6-транспорти-tcp--udp--both)
7. [Доставка та надійні канали](#7-доставка-та-надійні-канали)
8. [Розриви, reconnect, heartbeat](#8-розриви-reconnect-heartbeat)
9. [Продуктивність і порядок обробки](#9-продуктивність-і-порядок-обробки)
10. [Production-загартування](#10-production-загартування)
11. [Метрики](#11-метрики)
12. [Утиліти: GameLoopScheduler, EventManager](#12-утиліти)
13. [Повний довідник Configuration](#13-повний-довідник-configuration)
14. [Прод-чекліст](#14-прод-чекліст)
15. [Поширені помилки](#15-поширені-помилки)

---

## 1. Вимоги та встановлення

- **Бібліотека**: .NET Standard 2.1 (споживається .NET Core 3.0+/.NET 5-8, Unity, Mono, Xamarin/MAUI — **не** .NET Framework).
- **Споживачі/тести/приклади**: .NET 8.

```bash
dotnet add package SetNet
# серіалізатор (ядро його не містить) — напр. MessagePack-адаптер:
dotnet add package SetNet.MessagePack
```

> ℹ️ Ядро `SetNet` **не містить вбудованого серіалізатора**. Додайте `SetNet.MessagePack` (або власний `ISerializer`) і призначте його на старті — див. [розділ 4](#4-повідомлення-та-хендлери).

---

## 2. Базові концепти

| Тип | Роль |
|---|---|
| `BaseServer` | Слухає з'єднання, на кожного клієнта створює `BasePeer`. Ви наслідуєте й реалізуєте `OnNewClient`. |
| `BasePeer` | Серверне представлення одного клієнта: приймає його повідомлення, відповідає. |
| `BaseClient` | Клієнт: підключається, тримає lifecycle (connect/heartbeat/reconnect), приймає повідомлення. |
| `Configuration` | Усі налаштування (хост, порт, транспорт, ліміти, TLS…). |
| `[MessageHandler(type)]` | Атрибут на класі-хендлері для **односторонніх** повідомлень; реєстрація через рефлексію ([розділ 4](#4-повідомлення-та-хендлери)). |
| `[ProtocolChannel(channel)]` | Атрибут на класі, що обслуговує один **канал** уніфікованого протоколу (серверні op-и або клієнтські push-хендлери) — [розділ 5](#5-уніфікований-протокол-запитвідповідь-push-rpc). |
| `[Op(op)]` | Атрибут на методі, що обробляє одну операцію каналу (**запит/відповідь** або fire-and-forget). |
| `[Event(op)]` | Атрибут на клієнтському методі, що обробляє одну **push-подію** від сервера. |
| `[RpcMethod(id)]` | Атрибут на `IRpcHandler<TReq,TResp>` — фронтенд у стилі методу (`SetNet.Rpc`). |

**Потік повідомлення:** `SendAsync<T>` → серіалізація ([ваш `ISerializer`](#4-повідомлення-та-хендлери); напр. MessagePack) → фреймінг → транспорт → реасемблінг → десеріалізація → хендлер.

> ⚠️ **Порядок обробки за замовчуванням не гарантований** навіть на TCP (хендлери — fire-and-forget). Див. [розділ 9](#9-продуктивність-і-порядок-обробки).

---

## 3. Швидкий старт

### Крок 1. Типи повідомлень

```csharp
public enum MessageTypes : ushort
{
    PlayerMove = 1,
    ChatMessage = 2,
}

[MessagePackObject]
public class PlayerMoveMessage
{
    [Key(0)] public float X { get; set; }
    [Key(1)] public float Y { get; set; }
}
```

> При використанні MessagePack-серіалізатора DTO **мають** бути `[MessagePackObject]` з `[Key(n)]` на кожному полі (або `[MessagePackObject(true)]` для key-as-name). Для іншого серіалізатора вимоги диктує він — див. [розділ 4](#4-повідомлення-та-хендлери).

### Крок 2. Сервер

```csharp
using SetNet.Core;
using SetNet.Config;

public class GamePeer : BasePeer
{
    public GamePeer(PeerInfo info) : base(info) { }
    protected override void OnDisconnected() => Console.WriteLine($"{CurrentPeerInfo.Id} вийшов");
    protected override void OnError(string e) => Console.WriteLine(e);
    public Task PushAsync<T>(ushort type, T msg) => SendAsync(type, msg); // публічна обгортка над protected SendAsync
}

public class GameServer : BaseServer
{
    public GameServer(Configuration config) : base(config) { }
    protected override BasePeer OnNewClient(PeerInfo peerInfo) => new GamePeer(peerInfo);
    // StartReceive() викликає сам фреймворк — вручну не потрібно (але виклик безпечний, ідемпотентний)
}

var config = new Configuration { Host = "0.0.0.0", Port = 5682 };
var server = new GameServer(config);
await server.StartAsync(); // довготривалий цикл прийому
```

### Крок 3. Клієнт

```csharp
public class GameClient : BaseClient
{
    public GameClient(Configuration config) : base(config) { }

    protected override void OnConnected()    => Console.WriteLine("Підключено");
    protected override void OnDisconnected() => Console.WriteLine("Відключено");
    protected override void OnError(string e)=> Console.WriteLine($"Помилка: {e}");

    public Task MoveAsync(float x, float y)
        => SendAsync((ushort)MessageTypes.PlayerMove, new PlayerMoveMessage { X = x, Y = y });
}

var client = new GameClient(new Configuration { Host = "127.0.0.1", Port = 5682 });
await client.ConnectAsync();
await client.MoveAsync(10, 20);
```

---

## 4. Повідомлення та хендлери

Хендлери знаходяться рефлексією за замовчуванням або реєструються явно через `SetNetRuntime.Handlers`. Хендлер — це клас із `[MessageHandler]`, що реалізує `IServerMessageHandler<T>` чи `IClientMessageHandler<T>`. Хендлер **типізований**: бібліотека сама десеріалізує payload і віддає готовий `T` — вручну десеріалізувати не треба.

> `[MessageHandler]` — це **односторонній** тип повідомлення (надіслали, обробили, назад нічого). Для запит/відповідь, оп каналів, push-подій із сервера та RPC — див. [розділ 5](#5-уніфікований-протокол-запитвідповідь-push-rpc).

### Серверний хендлер

```csharp
using SetNet.Core;
using SetNet.Data;
using SetNet.Data.Attributes;

[MessageHandler((ushort)MessageTypes.PlayerMove)]
public class PlayerMoveHandler : IServerMessageHandler<PlayerMoveMessage>
{
    public async Task HandleAsync(BasePeer peer, PlayerMoveMessage msg)
    {
        // обробка; за потреби відповідь:
        await ((GamePeer)peer).PushAsync((ushort)MessageTypes.PlayerMove, msg);
    }
}
```

### Клієнтський хендлер

```csharp
[MessageHandler((ushort)MessageTypes.ChatMessage)]
public class ChatHandler : IClientMessageHandler<ChatMessage>
{
    public Task HandleAsync(ChatMessage msg)
    {
        Console.WriteLine(msg.Text);
        return Task.CompletedTask;
    }
}
```

**Якщо хендлер не викликається** — перевірте: (1) реалізує `IServerMessageHandler<T>`/`IClientMessageHandler<T>`; (2) має `[MessageHandler]` з правильним `ushort` або явно зареєстрований у `SetNetRuntime.Handlers`; (3) тип `T` та `ushort` збігаються з тим, що надсилається; (4) assembly хендлера завантажений або зареєстрований у runtime.

> ℹ️ За замовчуванням хендлери створюються через `Activator.CreateInstance` (потрібен публічний конструктор без параметрів) і **переюзаються як singleton** для всіх повідомлень цього типу. Для constructor injection використовуйте `SetNet.DependencyInjection`.

### Серіалізація — оберіть формат самі (MessagePack, JSON, …)

Ядро `SetNet` **не містить вбудованого серіалізатора** — формат обираєте ви через інтерфейс `ISerializer` (`SetNet.Messaging`):

```csharp
public interface ISerializer
{
    byte[] Serialize<T>(T value);
    T      Deserialize<T>(byte[] data);
}
```

Поки серіалізатор не призначено, типізоване надсилання і типізований dispatch хендлерів кидають `InvalidOperationException` із підказкою. Призначте його до підключення або старту сервера. `SetNetSerializer.Use(...)` налаштовує backward-compatible `SetNetRuntime.Default`; для ізольованих середовищ передайте власний `SetNetRuntime` у `Configuration.Runtime`.

**Варіант 1 — MessagePack (рекомендований)** через окремий пакет `SetNet.MessagePack`. Він дає `MessagePackNetSerializer`, загартований профілем безпеки `UntrustedData` (захист від DoS при десеріалізації):

```csharp
using SetNet.Messaging;
using SetNet.MessagePack;

SetNetSerializer.Use(new MessagePackNetSerializer());  // глобально, на старті
```

Scoped runtime:

```csharp
using SetNet;
using SetNet.Config;
using SetNet.MessagePack;

var runtime = new SetNetRuntime()
    .UseSerializer(new MessagePackNetSerializer());

runtime.Handlers.AutoDiscoverLoadedAssemblies = false;
runtime.Handlers.AddHandlersFromAssemblyOf<PlayerMoveHandler>();

var serverConfig = ConfigurationPresets.Development("0.0.0.0", 5000);
serverConfig.Runtime = runtime;

var clientConfig = ConfigurationPresets.Development("127.0.0.1", 5000);
clientConfig.Runtime = runtime;
```

Scoped runtime корисний, коли integration tests запускають кілька SetNet-стеків в одному процесі, коли plugin host хоче повністю явний каталог хендлерів, або коли два listener-и мають різні serializer/handler набори. В окремих процесах створіть сумісні runtime-и на обох боках.

**Варіант 2 — власний формат** (напр. System.Text.Json), без жодних залежностей:

```csharp
using SetNet.Messaging;
using System.Text.Json;

public sealed class MyJsonSerializer : ISerializer
{
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);
    public T Deserialize<T>(byte[] data) => JsonSerializer.Deserialize<T>(data)!;
}

SetNetSerializer.Use(new MyJsonSerializer());
```

**Правила:**
- Серіалізатор **один на runtime**. Якщо нічого спеціального не налаштовувати, застосунок використовує `SetNetRuntime.Default` через `SetNetSerializer.Use(...)`. Якщо задано `Configuration.Runtime`, endpoint використовує serializer і handler registry саме цього runtime.
- Хендлери **типізовані** — отримують готовий `T`, десеріалізувати вручну не треба (бібліотека робить це сама). Для ad-hoc випадків на default runtime доступні `SetNetSerializer.Serialize/Deserialize`; scoped-код може викликати `runtime.Serialize/Deserialize`.
- **Обидва боки** з'єднання мають використовувати один формат.
- Вимоги до DTO диктує обраний серіалізатор: для MessagePack — `[MessagePackObject]`/`[Key]` (див. вище); System.Text.Json працює зі звичайними публічними властивостями.

### Сирий доступ до кадрів — relay/proxy (`OnRawFrame` + `SendRawAsync`)

Іноді кадр треба **переслати, не дивлячись усередину** — наприклад relay-сервер у стилі Among Us переганяє ігровий трафік між гравцями лоббі. Десеріалізувати+знову серіалізувати тут марно. Для цього є два примітиви на `BaseClient`/`BasePeer`:

```csharp
// override на BaseSocket: викликається на КОЖЕН прикладний кадр (системні Ping/Pong/BindToken відсікаються)
// ДО типізованого диспетчингу. true = «спожито», типізований хендлер пропускається.
protected virtual bool OnRawFrame(ushort type, byte[] data);

// надіслати вже серіалізовані байти БЕЗ серіалізації
protected Task SendRawAsync(ushort type, byte[] payload, DeliveryMethod? delivery = null);
```

Relay-peer переганяє сирі байти й споживає кадр (нуль десеріалізації):

```csharp
public class RelayPeer : BasePeer
{
    private readonly RelayServer _server;
    public RelayPeer(PeerInfo info, RelayServer server) : base(info) { _server = server; }

    // публічна обгортка, щоб broadcast-цикл сервера міг переганяти сюди
    public Task ForwardAsync(ushort type, byte[] data) => SendRawAsync(type, data, DeliveryMethod.Unreliable);

    protected override bool OnRawFrame(ushort type, byte[] data)
    {
        _server.BroadcastRawToLobby(LobbyId, type, data, except: CurrentPeerInfo.Id);  // ваша політика
        return true;  // не передавати у типізований хендлер
    }
}
// BroadcastRawToLobby ітерує peer'ів лоббі й кличе peer.ForwardAsync(type, data)
```

**Правила:**
- `return false` (дефолт) → кадр іде далі у типізований хендлер. Звичайний код `OnRawFrame` не чіпає й **нічого не платить** (порожній віртуальний виклик).
- `return true` → типізований диспетчинг пропускається. Десеріалізації **не відбувається** взагалі.
- Можна й гібрид: контрольні повідомлення (join/ready/kick) — типізовані хендлери, ігрові — `OnRawFrame` + `SendRawAsync`. Перевіряйте `type` всередині.
- `OnRawFrame` виконується синхронно на receive-шляху — форвардьте fire-and-forget (`_ = SendRawAsync(...)`) або батчіть, не блокуйте.

---

## 5. Уніфікований протокол: запит/відповідь, push, RPC

Розділ 4 описує **односторонні** повідомлення: ви надіслали, інший бік обробив, назад нічого не приходить. Усе інше —
«спитати сервер і дочекатися відповіді», «повідомити сервер, відповідь не потрібна», «сервер сам штовхає подію
клієнтам» — іде через **уніфікований протокол** (простір імен `SetNet.Protocol`, частина ядра — встановлювати нічого
не треба).

Він займає **один** зарезервований wire-тип (`65447`) і всередині нього демультиплексується за **каналом** (`ushort`,
напр. ваш власний `World = 1000`) та **op** (`ushort`) у межах цього каналу. Усі готові модулі (Rooms, Inventory,
Chat, …) розмовляють саме так, тож ваші власні канали виглядають і поводяться так само.

### 5.1 Який тип повідомлення мені потрібен?

| Я хочу… | Клієнт | Сервер |
|---|---|---|
| Надіслати одностороннє повідомлення | `SendAsync<T>(type, msg)` | `[MessageHandler]` + `IServerMessageHandler<T>` (розділ 4) |
| Прийняти одностороннє повідомлення | `[MessageHandler]` + `IClientMessageHandler<T>` | `peer.SendAsync<T>(type, msg)` |
| **Спитати й дочекатися відповіді** | `RequestAsync<TReq,TResp>(channel, op, req)` | `[ProtocolChannel]` + метод `[Op]`, що **повертає** відповідь |
| **Повідомити сервер без відповіді** | `PostAsync<T>(channel, op, msg)` | метод `[Op]`, що повертає `void`/`Task` |
| **Приймати push від сервера** | `On<T>(channel, op, …)` або метод `[Event]` | `peer.PublishAsync(channel, op, evt)` |
| **Виклик у стилі методу** | `CallAsync<TReq,TResp>(methodId, req)` (`SetNet.Rpc`) | `[RpcMethod]` + `IRpcHandler<TReq,TResp>` |

Усе це працює **разом на одному з'єднанні**; повна мапа шарів і модулів — у [COMMUNICATION.md](COMMUNICATION.md).

### 5.2 Крок 1 — спільні контракти

Id та DTO мають збігатися на обох боках, тож оголосіть їх один раз у спільній збірці:

```csharp
using MessagePack;

public static class GameChannels
{
    public const ushort World = 1000;                    // ваш власний id каналу — див. 5.9
}

public enum WorldOp  : ushort { Drop = 1, Ready = 2 }    // клієнт → сервер
public enum WorldEvt : ushort { ItemDropped = 10 }       // сервер → клієнт

[MessagePackObject]
public class DropReq  { [Key(0)] public string ItemId { get; set; } = ""; [Key(1)] public int Count { get; set; } }

[MessagePackObject]
public class DropResp { [Key(0)] public bool Ok { get; set; } [Key(1)] public int Left { get; set; } }

[MessagePackObject]
public class ItemDropped { [Key(0)] public int PlayerId { get; set; } [Key(1)] public string ItemId { get; set; } = ""; }
```

### 5.3 Сервер — один метод на op (`[ProtocolChannel]` + `[Op]`)

Щоденний стиль: звичайний клас, позначений id каналу, і один метод на операцію — без `switch`, без базового класу,
без ручної реєстрації (знаходиться рефлексією, як `[MessageHandler]`).

```csharp
using SetNet.Core;
using SetNet.Protocol;

[ProtocolChannel(GameChannels.World)]
public sealed class WorldChannel
{
    // запит → відповідь: ЗНАЧЕННЯ, ЩО ПОВЕРТАЄТЬСЯ, і є відповіддю
    [Op((ushort)WorldOp.Drop)]
    public async Task<DropResp> Drop(BasePeer peer, DropReq req)
    {
        if (req.Count <= 0) throw new ProtocolException("Count must be positive.");   // → помилка у викликача

        var left = await Game.TryTakeAsync(peer, req.ItemId, req.Count);              // ваша авторитетна логіка
        if (left < 0) throw new ProtocolException("Not enough items.");

        return new DropResp { Ok = true, Left = left };   // (щоб ще й сповістити інших гравців — див. 5.6)
    }

    // fire-and-forget: void/Task не надсилає нічого назад (саме сюди б'є PostAsync)
    [Op((ushort)WorldOp.Ready)]
    public void Ready(BasePeer peer) => Game.MarkReady(peer);

    // сире всередину, сире назовні — серіалізатор взагалі не задіяний
    [Op(99)]
    public byte[] Echo(byte[] body) => body;
}
```

**Параметри** зв'язуються **за типом**, у будь-якому порядку, усі необов'язкові:

| Тип параметра | Що підставляється |
|---|---|
| `BasePeer` | peer, який надіслав повідомлення |
| `ChannelRequest` | повний контекст запиту (див. 5.4) |
| `byte[]` | сире, недесеріалізоване тіло |
| будь-що інше | тіло, десеріалізоване вашим серіалізатором (не більше одного такого параметра) |

**Тип повернення → відповідь:**

| Повертає | Ефект |
|---|---|
| `T` / `Task<T>` | серіалізується й надсилається як відповідь |
| `byte[]` / `Task<byte[]>` | надсилається як відповідь без змін (без серіалізації) |
| `void` / `Task` | відповіді немає — для fire-and-forget оп (або відповідайте самі через параметр `ChannelRequest`) |
| кидає виняток | `RequestAsync` у викликача кидає `ProtocolException` із вашим повідомленням |

### 5.4 Сервер — повний контроль (`IChannelService`)

Коли потрібна одна точка входу на весь канал (спільна підготовка, власна маршрутизація, op-и, що визначаються під час
виконання), реалізуйте `IChannelService`. Клас, який його реалізує, лишає керування за собою, а його методи `[Op]`
(якщо вони є) **ігноруються**.

```csharp
[ProtocolChannel(GameChannels.World)]
public sealed class WorldService : IChannelService
{
    public async Task HandleAsync(ChannelRequest r)
    {
        switch ((WorldOp)r.Op)
        {
            case WorldOp.Drop:
                var req = r.Read<DropReq>();                        // типізоване тіло … або r.RawBody для байтів
                await r.ReplyAsync(new DropResp { Ok = true });     // типізована відповідь … або r.ReplyRawAsync(bytes)
                break;

            case WorldOp.Ready:
                Game.MarkReady(r.Peer);                             // fire-and-forget: без відповіді
                break;

            default:
                if (r.ExpectsReply) await r.ReplyErrorAsync($"Unknown op {r.Op}");
                break;
        }
    }
}
```

`ChannelRequest`: `Peer`, `Channel`, `Op`, `RawBody`, `ExpectsReply`, `Read<T>()`, `ReplyAsync<T>(T)`,
`ReplyRawAsync(byte[])`, `ReplyErrorAsync(string)`. Відповідайте **щонайбільше один раз** — наступні виклики
ігноруються.

### 5.5 Клієнт — запит і post

```csharp
using SetNet.Protocol;

// запит → відповідь (корельовано, завжди Reliable, з тайм-аутом)
DropResp resp = await client.RequestAsync<DropReq, DropResp>(
    GameChannels.World, (ushort)WorldOp.Drop,
    new DropReq { ItemId = "sword", Count = 1 },
    timeoutMs: 10000);                       // дефолт 10 с; ≤ 0 = чекати нескінченно; є ще CancellationToken

byte[] raw = await client.RequestRawAsync(GameChannels.World, 99, new byte[] { 1, 2, 3 });   // без серіалізатора

// fire-and-forget — єдина форма, де ви обираєте надійність
await client.PostAsync(GameChannels.World, (ushort)WorldOp.Ready, new ReadyDto());
await client.PostRawAsync(GameChannels.World, (ushort)WorldOp.Ready, bytes, DeliveryMethod.Unreliable);
```

### 5.6 Push із сервера та підписка на клієнті

Сервер — штовхнути одному peer'у або багатьом:

```csharp
await peer.PublishAsync(GameChannels.World, (ushort)WorldEvt.ItemDropped, evt);       // один клієнт, типізовано
await peer.PublishRawAsync(GameChannels.World, (ushort)WorldEvt.ItemDropped, bytes);  // один клієнт, сиро

IEnumerable<BasePeer> others = server.OthersInRoomOf(peer);   // хелпер SetNet.Rooms — або ваш власний список peer'ів
await others.PublishAsync(GameChannels.World, (ushort)WorldEvt.ItemDropped, evt);     // фан-аут, best-effort
```

Клієнт — два стилі, і **обидва** спрацьовують на один і той самий `(channel, op)`:

```csharp
// (а) імперативний — може замикатися на стані, повертає IDisposable для відписки
IDisposable sub = client.On<ItemDropped>(GameChannels.World, (ushort)WorldEvt.ItemDropped, e => Render(e));
client.OnRaw(GameChannels.World, 99, bytes => { /* декодуєте самі */ });
// sub.Dispose();   // відписка

// (б) декларативний — клас [ProtocolChannel] із методами [Event], підписується сам на першій події
[ProtocolChannel(GameChannels.World)]
public sealed class WorldEvents
{
    [Event((ushort)WorldEvt.ItemDropped)] public void OnDropped(ItemDropped e) => Render(e);
    [Event(99)]                           public void OnBlob(byte[] body)      { /* сире тіло */ }
}
```

Метод `[Event]` приймає типізоване тіло, `byte[]` або взагалі не має параметрів і повертає `void` чи `Task` (асинхронні
хендлери йдуть fire-and-forget; виняток в одному з них ізольований). Його екземпляри — **singleton на весь процес**,
тож коли хендлер має замикатися на стані конкретного екземпляра (напр. драйвер, що тримає стан кімнати), беріть
стиль (а).

### 5.7 Помилки й тайм-аути

| На сервері | У викликача (`RequestAsync`) |
|---|---|
| хендлер кидає (будь-який виняток) | `ProtocolException` із текстом винятку |
| `throw new ProtocolException("…")` | те саме — це і є навмисний спосіб завалити запит |
| жоден `[Op]` не збігся з op | `ProtocolException("No [Op(N)] handler on channel C.")` |
| для каналу немає сервісу | `ProtocolException("No protocol channel C is configured on this server.")` |
| op ніколи не відповідає (повертає `void`/`Task`) | `TimeoutException` після `timeoutMs` |

Fire-and-forget `PostAsync` на невідомий op тихо ігнорується — на нього ніхто не чекає. Готові модулі перемапують
`ProtocolException` у власний тип (`RoomException`, `RpcException`, …); робіть так само у власному клієнтському
драйвері, якщо хочете доменний тип помилки.

### 5.8 RPC — типізований аліас `RequestAsync` (`SetNet.Rpc`)

Якщо фронтенд із id методу читається краще, ніж канал + op: `client.CallAsync<TReq,TResp>(id, req)` **це і є**
`client.RequestAsync<TReq,TResp>(Channels.Rpc, id, req)` — той самий конверт і та сама кореляція, лише `RpcException`
замість `ProtocolException` і дефолтний тайм-аут 5 с.

```bash
dotnet add package SetNet.Rpc
```

```csharp
using SetNet.Rpc;

RpcRuntime.Enable();      // один раз на старті, на обох боках

// клієнт
var resp = await client.CallAsync<LoginReq, LoginResp>(1, new LoginReq { Name = "alice" });

// сервер
[RpcMethod(1)]
public class LoginHandler : IRpcHandler<LoginReq, LoginResp>
{
    public Task<LoginResp> HandleAsync(BasePeer peer, LoginReq req)
        => Task.FromResult(new LoginResp { Ok = true });
}
```

### 5.9 Правила й підводні камені

- **Id каналів приблизно від 1000 — ваші.** 1–34 зайняті готовими модулями (`SetNet.Protocol.Channels`). Простір
  каналів незалежний від простору `ushort`-типів повідомлень ядра, тож `GameChannels.World = 1000` і
  `MessageTypes.PlayerMove = 1` ніколи не конфліктують.
- **Один сервіс на id каналу.** Серверні класи `[ProtocolChannel]` знаходяться скануванням завантажених збірок
  (для каналу перемагає знайдений останнім) — не через `runtime.Handlers`. Клас вважається *серверним* каналом лише
  якщо реалізує `IChannelService` або має принаймні один метод `[Op]`; клас лише з методами `[Event]` — клієнтський.
- **Op-и живуть у межах свого каналу.** `[Op(1)]` у двох різних каналах ніяк не пов'язані; дублікати *в одному* класі
  кидають виняток на етапі знаходження.
- **Надійність.** `Request*` і `Publish*` — завжди `Reliable`; лише `Post*` дозволяє обрати `Unreliable`.
  Високочастотний стан (позиції, здоров'я) — це `SetNet.StateSync` або шар ядра, а не сюди.
- **Типізовано vs сиро.** Типізовані перевантаження використовують серіалізатор endpoint'а, тож `T` має відповідати
  його вимогам (MessagePack: `[MessagePackObject]`/`[Key]`). Родина `*Raw*` (`RequestRawAsync`, `PostRawAsync`,
  `OnRaw`, `RawBody`, `ReplyRawAsync`) від серіалізатора не залежить — зручно для контрольних повідомлень із ручним
  фреймінгом.
- **Готовим модулям потрібен їхній `Enable()`.** Викличте `XxxRuntime.Enable()` один раз на старті, щоб збірка модуля
  була завантажена й доступна для знаходження. Вашим власним каналам у вашій же збірці не потрібно нічого.
- **Екземпляри хендлерів — singleton**, створюються тим самим активатором, що й `[MessageHandler]` — для constructor
  injection беріть `SetNet.DependencyInjection`.

---

## 6. Транспорти: TCP / UDP / Both

Обирається через `Configuration.TransportType` (дефолт `Tcp` — наявний TCP-код працює без змін).

```csharp
var config = new Configuration
{
    Host = "127.0.0.1", Port = 5682,
    TransportType = TransportType.Both,   // Tcp | Udp | Both
    UdpReliabilityEnabled = true,
    DefaultDelivery = DeliveryMethod.Reliable
};
```

**Маршрутизація `(TransportType, DeliveryMethod)`:**

| TransportType | DeliveryMethod | Канал |
|---|---|---|
| Tcp  | будь-який | TCP |
| Udp  | Reliable | Шар надійності UDP (потрібен `UdpReliabilityEnabled`, інакше `Validate()` кидає) |
| Udp  | Unreliable | Сира UDP-датаграма |
| Both | Reliable | TCP |
| Both | Unreliable | UDP (відкат на TCP, поки UDP-канал не приєднано) |

Особливості:
- **UDP — емуляція з'єднання**: handshake призначає ідентичність, heartbeat — живість, тож `OnConnected`/`OnDisconnected`/`BasePeer` працюють як у TCP.
- **Both**: спершу TCP, сервер передає UDP-токен по TCP, UDP-handshake прив'язується до того ж peer. Якщо UDP недоступний — плавний відкат на TCP-only.
- **MTU**: датаграми > `UdpMaxDatagramPayload` (1200 Б) відхиляються; фрагментації немає.

Спробувати локально:
```bash
dotnet run --project tests/SetNet.Tests -- <frag|tcp|udp|loss|both|idle|deadlock>
```

---

## 7. Доставка та надійні канали

`SendAsync` має перевантаження:

```csharp
await SendAsync(type, msg);                              // DefaultDelivery
await SendAsync(type, msg, DeliveryMethod.Unreliable);  // явний канал
await SendAsync(type, msg, DeliveryMethod.Reliable, channel: 1); // надійний UDP-канал 1
```

### Незалежні надійні UDP-канали (`UdpReliableChannels`)

Кожен канал має власні sequence/ACK/порядок, тож втрата на одному не блокує інший:

```csharp
var config = new Configuration
{
    TransportType = TransportType.Udp, UdpReliabilityEnabled = true,
    UdpReliableChannels = 2   // канали 0 і 1 — незалежні впорядковані потоки
};
await SendAsync(type, movement, DeliveryMethod.Reliable, channel: 0);
await SendAsync(type, chat,     DeliveryMethod.Reliable, channel: 1);
```

> Надійний UDP має приймальне вікно й back-pressure: відправник не може випередити «найстаршу дірку» більш ніж на `UdpReliableWindowSize` послідовностей.

---

## 8. Розриви, reconnect, heartbeat

`BaseClient` розрізняє навмисний `Disconnect()` від неочікуваної втрати. **`OnDisconnected` спрацьовує рівно один раз** на з'єднання.

```csharp
public class GameClient : BaseClient
{
    public GameClient(Configuration config) : base(config) { }
    protected override void OnConnected() { }
    protected override void OnDisconnected() { }                  // закрито (будь-яка причина)
    protected override void OnError(string e) { }                 // тільки неочікувана помилка
    protected override void OnUnexpectedDisconnect() { }          // сервер впав / мережа
    protected override void OnReconnecting(int a, int max) { }    // перед кожною спробою
    protected override void OnReconnected() { }                   // успіх
    protected override void OnReconnectFailed() { }               // усі спроби вичерпано
    protected override void OnStateChanged(ConnectionState f, ConnectionState t) { }
}
```

| Подія | OnError | OnUnexpectedDisconnect | OnDisconnected | Auto-Reconnect |
|---|---|---|---|---|
| `Disconnect()` (навмисно) | ❌ | ❌ | ✅ | ❌ |
| Помилка мережі / краш сервера | ✅ | ✅ | ✅ (якщо reconnect провалився) | ✅ (якщо увімкнено) |
| Graceful close сервером | ❌ | ❌ | ✅ | ❌ |

Авто-reconnect:
```csharp
var config = new Configuration
{
    AutoReconnect = true, MaxReconnectAttempts = 5, ReconnectDelayMs = 1000
};
```

**Heartbeat** (виявлення «мертвих» з'єднань) — **типово вимкнено**:
```csharp
var config = new Configuration { HeartbeatEnabled = true, HeartbeatIntervalMs = 5000, HeartbeatTimeoutMs = 15000 };
```

На сервері `BasePeer` симетрично: `Close()` (kick) → лише `OnDisconnected`; краш клієнта/IO-помилка → `OnError` + `OnUnexpectedDisconnect` + `OnDisconnected`.

---

## 9. Продуктивність і порядок обробки

Усі прапорці нижче — opt-in (дефолт зберігає початкову поведінку).

### Порядок обробки (`SequentialDispatch`)
> ⚠️ Дефолт: хендлери fire-and-forget, **порядок не гарантований навіть на TCP**.

```csharp
var config = new Configuration { SequentialDispatch = true };
// receive-loop чекає завершення кожного хендлера перед наступним кадром (строгий порядок, менший паралелізм)
```

### Back-pressure (`MaxInFlightMessages`)
```csharp
var config = new Configuration { MaxInFlightMessages = 256 };
// межа одночасних хендлерів на з'єднання; при досягненні receive-loop призупиняється
```

### Батчинг (`SendBatching`) — для game-tick
```csharp
var config = new Configuration { SendBatching = true, SendBatchFlushMs = 15 };
// ... за тік:
await SendAsync(t1, m1);
await SendAsync(t2, m2);   // акумулюються в буфер
await FlushAsync();        // один запис у сокет (на BaseClient/BasePeer)
```
Дає найвищу пропускну здатність (~1.8M msgs/сек проти ~240k без батчингу).

### Тайм-аут надсилання (`SendTimeoutMs`, дефолт 30000)
Обмежує час одного запису в сокет — «застряглий» peer не блокує відправку назавжди. `0` вимикає.

### Nagle (`TcpNoDelay`, дефолт `true`)
Вимкнений Nagle = низька затримка дрібних кадрів. Для масового потоку незабатчених повідомлень `false` дає вищий throughput (але +затримка). Деталі — у [PERFORMANCE.ua.md](PERFORMANCE.ua.md).

---

## 10. Production-загартування

```csharp
using System.Security.Cryptography.X509Certificates;

var config = ConfigurationPresets.ProductionTcp("0.0.0.0", 5682);
config.ServerCertificate = new X509Certificate2("server.pfx", "password");

// Або зберіть вручну:
config = new Configuration
{
    Host = "0.0.0.0", Port = 5682,

    // TLS поверх TCP (UDP НЕ шифрується)
    UseSsl = true,
    ServerCertificate = new X509Certificate2("server.pfx", "password"), // на сервері
    // на клієнті: SslTargetHost / ServerCertificateValidationCallback

    // Ліміти / захист від DoS
    MaxConnectionsLimit = 5000,
    MaxUdpPeers = 5000,
    MaxMessageSize = 1024 * 1024,
    MaxConnectionsPerIpPerSecond = 20,
    MaxInFlightMessages = 256,
    MaxInboundQueue = 16384,   // межа вхідної черги на з'єднання (захист від OOM)
};

foreach (var issue in config.AnalyzeProduction())
    Console.WriteLine(issue);

config.ValidateProduction(); // кидає, якщо лишились production-blocking помилки
```

- **Автентифікація — на боці застосунку**: перевіряйте креденшіали у вашому `OnNewClient`/хендлерах (бібліотека дає лише транспорт).
- **UDP без шифрування й per-packet автентифікації** — чутливі дані тільки через TLS-over-TCP (або Both з reliable, що йде по TCP).
- **Стійкість**: збій `OnNewClient`/`StartReceive` не валить accept-loop; кривий TLS-handshake не кладе сервер; помилки reconnect/heartbeat логуються; виняток у хендлері/user-хуку не рве cleanup; обмежена вхідна черга захищає від OOM.

---

## 11. Метрики

```csharp
var m = config.Metrics; // NetworkMetrics, потокобезпечні лічильники
Console.WriteLine(m.Snapshot()); // sent/recv/accepted/rejected/retransmits/acks/handshakesDropped/inboundDropped
int live = server.ActiveConnections;
```

Найкорисніше для прода: `InboundDropped` (перевантаження), `ConnectionsRejected` (ліміти/rate-limit), `ReliableRetransmits` (втрати UDP), `HandshakesDropped` (UDP-флуд).

---

## 12. Утиліти

### GameLoopScheduler — періодичні задачі
```csharp
using SetNet.Utils;
var scheduler = new GameLoopScheduler();
scheduler.Every(100, async () => { /* server tick */ await Task.CompletedTask; });
scheduler.StartInBackground();
// await scheduler.StopAsync();
```

### EventManager — in-process pub/sub
```csharp
using SetNet.Events;
var ev = new EventManager();
ev.Subscribe("PlayerJoined", data => { /* ... */ });
ev.Trigger("PlayerJoined", "Alex");
```
> ⚠️ `EventManager` — in-process і **не thread-safe**; це не мережевий pub/sub. Для виклику з кількох потоків синхронізуйте самі.

---

## 13. Повний довідник Configuration

| Опція | Дефолт | Призначення |
|---|---|---|
| `Host` / `Port` | — | Endpoint (TCP; UDP теж, якщо `UdpPort=0`). |
| `BufferSize` | 4096 | Розмір буфера читання. |
| `TcpNoDelay` | `true` | Вимкнути Nagle (низька затримка). |
| `TransportType` | `Tcp` | `Tcp` \| `Udp` \| `Both`. |
| `DefaultDelivery` | `Reliable` | Для 2-арг `SendAsync(type, msg)`. |
| `UdpPort` | 0 | 0 = використати `Port`. |
| `UdpReliabilityEnabled` | `true` | Майстер-тумблер надійного UDP. |
| `UdpReliableChannels` | 1 | К-сть незалежних надійних каналів. |
| `UdpReliableWindowSize` | 64 | Вікно (1..64). |
| `UdpReliableAckTimeoutMs` | 100 | Таймаут до ретрансміту. |
| `UdpReliableMaxRetransmits` | 10 | Стеля ретрансмітів → onFailure. |
| `UdpMaxDatagramPayload` | 1200 | Макс. датаграма (без фрагментації). |
| `UdpOrderedReliable` | `true` | Впорядкована надійна доставка. |
| `UdpHandshakeTimeoutMs` | 5000 | Таймаут UDP-handshake. |
| `UdpPeerExpiryMs` | 15000 | Простій до видалення UDP-peer. |
| `HeartbeatEnabled` | `false` | Ping/Pong для виявлення мертвих з'єднань. |
| `HeartbeatIntervalMs` / `HeartbeatTimeoutMs` | 5000 / 15000 | Інтервал / таймаут heartbeat. |
| `AutoReconnect` | `false` | Авто-reconnect клієнта. |
| `MaxReconnectAttempts` / `ReconnectDelayMs` | 3 / 1000 | Політика reconnect. |
| `ConnectTimeoutMs` | 10000 | Таймаут connect/handshake. |
| `MaxInFlightMessages` | 0 | Back-pressure (0 = необмежено). |
| `SequentialDispatch` | `false` | Строгий порядок обробки. |
| `SendBatching` / `SendBatchFlushMs` | `false` / 15 | Коалесований TCP-запис. |
| `SendTimeoutMs` | 30000 | Межа на один запис у сокет (0 = вимк.). |
| `MaxInboundQueue` | 16384 | Межа вхідної черги (OOM-захист). |
| `UseSsl` | `false` | TLS поверх TCP. |
| `ServerCertificate` / `SslTargetHost` / `ServerCertificateValidationCallback` | null | TLS-параметри. |
| `MaxConnections` | 100 | Базова стеля з'єднань. |
| `MaxConnectionsLimit` | 0 | Якщо >0 — переважає `MaxConnections`. |
| `MaxUdpPeers` | 1000 | Стеля UDP-peer'ів. |
| `MaxMessageSize` | 1 MiB | Стеля TCP-кадру. |
| `MaxConnectionsPerIpPerSecond` | 0 | Per-IP rate-limit (0 = вимк.). |
| `Logger` | `ConsoleLogger` | Логування (`ILogger`). |
| `Metrics` | — | `NetworkMetrics` лічильники. |

`Validate()` викликається на connect/start і fail-fast перевіряє несумісні налаштування.

---

## 14. Прод-чекліст

Дефолти оптимізовані під сумісність, не під прод. Перед запуском:

- [ ] Реалізувати **авторизацію** в `OnNewClient`/хендлерах.
- [ ] `HeartbeatEnabled = true` (інакше мертві з'єднання не виявляються).
- [ ] `MaxInFlightMessages > 0` (інакше необмежені fire-and-forget Task'и під навантаженням).
- [ ] `MaxConnectionsLimit`, `MaxConnectionsPerIpPerSecond` під вашу ємність.
- [ ] `UseSsl = true` + сертифікат, якщо поза довіреною мережею (і **не** слати чутливе по UDP).
- [ ] Експорт `config.Metrics.Snapshot()` у моніторинг.
- [ ] **Soak/load-тест** на реальному трафіку перед повним запуском.

Детальні межі масштабування — у [PERFORMANCE.ua.md](PERFORMANCE.ua.md).

---

## 15. Поширені помилки

| Симптом | Причина / розв'язання |
|---|---|
| Хендлер не викликається | Немає `[MessageHandler]`, не той тип, не реалізує інтерфейс, або клас/assembly не завантажений і не зареєстрований у `SetNetRuntime.Handlers`. |
| `ProtocolException: No protocol channel N is configured on this server` | На сервері немає класу `[ProtocolChannel(N)]`, його збірка не завантажена (для готового модуля викличте `XxxRuntime.Enable()`), або клас не реалізує `IChannelService` і не має жодного методу `[Op]`. |
| `RequestAsync` кидає `TimeoutException` | Метод `[Op]` повертає `void`/`Task`, тож ніколи не відповідає (такі повідомлення шліть через `PostAsync`), не збігається id оп, або хендлер повільніший за `timeoutMs`. |
| `On<T>` / `[Event]` не спрацьовує | На класі немає `[ProtocolChannel]`, `(channel, op)` не збігається з серверним `PublishAsync`, або `IDisposable` підписки вже звільнено. |
| `[Event]`-хендлер бачить не той стан | Екземпляри `[Event]`-хендлерів — singleton на весь процес; якщо хендлер має замикатися на стані екземпляра, беріть `client.On<T>(...)` (розділ 5.6). |
| Повідомлення «б'ються» | Різні серіалізатори на двох боках; (MessagePack) DTO без `[MessagePackObject]`/`[Key]`; або тип не збігається. |
| `InvalidOperationException: No serializer configured` | На endpoint runtime немає serializer-а — викличте `SetNetSerializer.Use(...)` для default runtime або `runtime.UseSerializer(...)` перед присвоєнням `Configuration.Runtime` (див. розділ 4). |
| Не підключається | Host/Port різні на клієнті й сервері; брандмауер; (UDP) handshake блокується. |
| Обробка не в порядку | Це дефолтна поведінка — увімкніть `SequentialDispatch`. |
| Reliable-UDP кидає на надсиланні | `DefaultDelivery=Reliable` + `UdpReliabilityEnabled=false` на чистому UDP. Validate() це ловить. |
| OOM під флудом | Перевірте `MaxInboundQueue`, `MaxUdpPeers`, `MaxMessageSize`, `MaxConnectionsPerIpPerSecond`. |

---

Приклад повноцінного чату (окремо сервер і клієнт) — у теці [`examples/`](../examples). Архітектура й структура проекту — у [CLAUDE.md](../CLAUDE.md).
