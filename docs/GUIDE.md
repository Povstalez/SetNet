# SetNet — детальний посібник користувача

Повна інструкція з використання бібліотеки: від «hello world» до production-конфігурації.
Короткий огляд — у [README](../README.md); продуктивність і межі масштабування — у [PERFORMANCE.md](PERFORMANCE.md).

## Зміст
1. [Вимоги та встановлення](#1-вимоги-та-встановлення)
2. [Базові концепти](#2-базові-концепти)
3. [Швидкий старт](#3-швидкий-старт)
4. [Повідомлення та хендлери](#4-повідомлення-та-хендлери)
5. [Транспорти: TCP / UDP / Both](#5-транспорти-tcp--udp--both)
6. [Доставка та надійні канали](#6-доставка-та-надійні-канали)
7. [Розриви, reconnect, heartbeat](#7-розриви-reconnect-heartbeat)
8. [Продуктивність і порядок обробки](#8-продуктивність-і-порядок-обробки)
9. [Production-загартування](#9-production-загартування)
10. [Метрики](#10-метрики)
11. [Утиліти: GameLoopScheduler, EventManager](#11-утиліти)
12. [Повний довідник Configuration](#12-повний-довідник-configuration)
13. [Прод-чекліст](#13-прод-чекліст)
14. [Поширені помилки](#14-поширені-помилки)

---

## 1. Вимоги та встановлення

- **Бібліотека**: .NET Standard 2.1 (споживається .NET Core 3.0+/.NET 5-8, Unity, Mono, Xamarin/MAUI — **не** .NET Framework).
- **Споживачі/тести/приклади**: .NET 8.

```bash
# локально — посилання на проект
dotnet add reference ../SetNet/SetNet.csproj
```

---

## 2. Базові концепти

| Тип | Роль |
|---|---|
| `BaseServer` | Слухає з'єднання, на кожного клієнта створює `BasePeer`. Ви наслідуєте й реалізуєте `OnNewClient`. |
| `BasePeer` | Серверне представлення одного клієнта: приймає його повідомлення, відповідає. |
| `BaseClient` | Клієнт: підключається, тримає lifecycle (connect/heartbeat/reconnect), приймає повідомлення. |
| `Configuration` | Усі налаштування (хост, порт, транспорт, ліміти, TLS…). |
| `[MessageHandler(type)]` | Атрибут на класі-хендлері; реєстрація через рефлексію. |

**Потік повідомлення:** `SendAsync<T>` → серіалізація (MessagePack за замовч., [змінна](#4-повідомлення-та-хендлери)) → фреймінг → транспорт → реасемблінг → десеріалізація → хендлер.

> ⚠️ **Порядок обробки за замовчуванням не гарантований** навіть на TCP (хендлери — fire-and-forget). Див. [розділ 8](#8-продуктивність-і-порядок-обробки).

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

> DTO **мають** бути `[MessagePackObject]` з `[Key(n)]` на кожному полі (або використовуйте `[MessagePackObject(true)]` для key-as-name).

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

Хендлери знаходяться рефлексією при старті — клас із `[MessageHandler]`, що реалізує потрібний інтерфейс.

### Серверний хендлер

```csharp
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;

[MessageHandler((ushort)MessageTypes.PlayerMove)]
public class PlayerMoveHandler : IServerMessageHandler
{
    public async Task HandleAsync(BasePeer peer, byte[] data)
    {
        var msg = SetNetSerializer.Deserialize<PlayerMoveMessage>(data);
        // обробка; за потреби відповідь:
        await ((GamePeer)peer).PushAsync((ushort)MessageTypes.PlayerMove, msg);
    }
}
```

### Клієнтський хендлер

```csharp
[MessageHandler((ushort)MessageTypes.ChatMessage)]
public class ChatHandler : IClientMessageHandler
{
    public Task HandleAsync(byte[] data)
    {
        var msg = SetNetSerializer.Deserialize<ChatMessage>(data);
        Console.WriteLine(msg.Text);
        return Task.CompletedTask;
    }
}
```

**Якщо хендлер не викликається** — перевірте: (1) реалізує `IServerMessageHandler`/`IClientMessageHandler`; (2) має `[MessageHandler]` з правильним `ushort`; (3) тип збігається з тим, що надсилається; (4) клас у завантаженому assembly.

> ℹ️ Хендлери створюються через `Activator.CreateInstance` (потрібен публічний конструктор без параметрів) і **переюзаються як singleton** для всіх повідомлень цього типу. **DI у конструктор немає** — резолвіть сервіси через статичний service-locator чи власний механізм.

### Серіалізація — за замовчуванням MessagePack, але змінна

Бібліотека серіалізує/десеріалізує через інтерфейс `ISerializer` (`SetNet.Messaging`):

```csharp
public interface ISerializer
{
    byte[] Serialize<T>(T value);
    T      Deserialize<T>(byte[] data);
}
```

За замовчуванням активний `MessagePackNetSerializer` (байт-у-байт ідентичний попередній статичній MessagePack-поведінці — нічого міняти не треба). Щоб перевести **весь застосунок** на інший формат, призначте власний серіалізатор **один раз на старті**, до підключення:

```csharp
// Глобально: торкається всіх з'єднань і статичних хелперів SetNetSerializer.Serialize/Deserialize
SetNetSerializer.Default = new MyJsonSerializer();
```

Приклад власного серіалізатора (System.Text.Json):

```csharp
using SetNet.Messaging;
using System.Text.Json;

public sealed class MyJsonSerializer : ISerializer
{
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);
    public T Deserialize<T>(byte[] data) => JsonSerializer.Deserialize<T>(data)!;
}
```

Або **на конкретне з'єднання** через конфіг (зручно, коли різні сервери/клієнти мають різний формат):

```csharp
var config = new Configuration { /* ... */ Serializer = new MyJsonSerializer() };
```

**Правила:**
- У хендлерах десеріалізуйте через фасад `SetNetSerializer.Deserialize<T>(data)` — він поважає `SetNetSerializer.Default` і не прив'язує код до конкретного формату. (Хендлер не має посилання на з'єднання, тож per-connection серіалізатор він не бачить.) На сервері, якщо ви використовуєте **per-connection** серіалізатор, десеріалізуйте через `peer.CurrentPeerInfo.Config.Serializer.Deserialize<T>(data)`.
- `Configuration.Serializer` без явного значення повертається до `SetNetSerializer.Default`.
- **Обидва боки** з'єднання мають використовувати один формат.
- Якщо лишаєте MessagePack — ваші DTO мають бути `[MessagePackObject]`/`[Key]` (див. вище). Для JSON/інших форматів вимоги диктує вже ваш серіалізатор (System.Text.Json працює зі звичайними публічними властивостями).

---

## 5. Транспорти: TCP / UDP / Both

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
dotnet run --project SetNet.Tests -- <frag|tcp|udp|loss|both|idle|deadlock>
```

---

## 6. Доставка та надійні канали

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

## 7. Розриви, reconnect, heartbeat

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

## 8. Продуктивність і порядок обробки

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
Вимкнений Nagle = низька затримка дрібних кадрів. Для масового потоку незабатчених повідомлень `false` дає вищий throughput (але +затримка). Деталі — у [PERFORMANCE.md](PERFORMANCE.md).

---

## 9. Production-загартування

```csharp
using System.Security.Cryptography.X509Certificates;

var config = new Configuration
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
```

- **Автентифікація — на боці застосунку**: перевіряйте креденшіали у вашому `OnNewClient`/хендлерах (бібліотека дає лише транспорт).
- **UDP без шифрування й per-packet автентифікації** — чутливі дані тільки через TLS-over-TCP (або Both з reliable, що йде по TCP).
- **Стійкість**: збій `OnNewClient`/`StartReceive` не валить accept-loop; кривий TLS-handshake не кладе сервер; помилки reconnect/heartbeat логуються; виняток у хендлері/user-хуку не рве cleanup; обмежена вхідна черга захищає від OOM.

---

## 10. Метрики

```csharp
var m = config.Metrics; // NetworkMetrics, потокобезпечні лічильники
Console.WriteLine(m.Snapshot()); // sent/recv/accepted/rejected/retransmits/acks/handshakesDropped/inboundDropped
int live = server.ActiveConnections;
```

Найкорисніше для прода: `InboundDropped` (перевантаження), `ConnectionsRejected` (ліміти/rate-limit), `ReliableRetransmits` (втрати UDP), `HandshakesDropped` (UDP-флуд).

---

## 11. Утиліти

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

## 12. Повний довідник Configuration

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

## 13. Прод-чекліст

Дефолти оптимізовані під сумісність, не під прод. Перед запуском:

- [ ] Реалізувати **авторизацію** в `OnNewClient`/хендлерах.
- [ ] `HeartbeatEnabled = true` (інакше мертві з'єднання не виявляються).
- [ ] `MaxInFlightMessages > 0` (інакше необмежені fire-and-forget Task'и під навантаженням).
- [ ] `MaxConnectionsLimit`, `MaxConnectionsPerIpPerSecond` під вашу ємність.
- [ ] `UseSsl = true` + сертифікат, якщо поза довіреною мережею (і **не** слати чутливе по UDP).
- [ ] Експорт `config.Metrics.Snapshot()` у моніторинг.
- [ ] **Soak/load-тест** на реальному трафіку перед повним запуском.

Детальні межі масштабування — у [PERFORMANCE.md](PERFORMANCE.md).

---

## 14. Поширені помилки

| Симптом | Причина / розв'язання |
|---|---|
| Хендлер не викликається | Немає `[MessageHandler]`, не той тип, не реалізує інтерфейс, або клас не в завантаженому assembly. |
| Повідомлення «б'ються» | DTO без `[MessagePackObject]`/`[Key]`, або тип не збігається на двох боках. |
| Не підключається | Host/Port різні на клієнті й сервері; брандмауер; (UDP) handshake блокується. |
| Обробка не в порядку | Це дефолтна поведінка — увімкніть `SequentialDispatch`. |
| Reliable-UDP кидає на надсиланні | `DefaultDelivery=Reliable` + `UdpReliabilityEnabled=false` на чистому UDP. Validate() це ловить. |
| OOM під флудом | Перевірте `MaxInboundQueue`, `MaxUdpPeers`, `MaxMessageSize`, `MaxConnectionsPerIpPerSecond`. |

---

Приклад повноцінного чату (окремо сервер і клієнт) — у теці [`examples/`](../examples). Архітектура й структура проекту — у [CLAUDE.md](../CLAUDE.md).
