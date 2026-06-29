# SetNet 🌐

Потужна та легка в використанні .NET бібліотека для клієнт-серверної комунікації через **TCP, UDP або обидва транспорти одночасно** з автоматичною реєстрацією обробників повідомлень.

## Особливості

✨ **Простота використання** - Мінімум коду для налаштування мережевої комунікації  
🚦 **TCP / UDP / Both** - Обирається через `Configuration.TransportType`; вибір каналу на повідомлення через `DeliveryMethod`  
🛡️ **Опціональна надійність UDP** - Sequence/ACK/retransmit/ordered, вмикається через конфіг  
🔄 **Автоматична реєстрація** - Обробники повідомлень реєструються через рефлексію  
💾 **MessagePack серіалізація** - Ефективна серіалізація даних  
🎮 **GameLoopScheduler** - Вбудована система для запланованих завдань  
📡 **Асинхронна обробка** - Весь код базується на async/await  
🔌 **Гнучка архітектура** - Легко розширювати для своїх потреб  

## Вимоги

- **.NET Standard 2.1** або вище
- **C# 8.0** або вище

## Встановлення

### Через NuGet (у майбутньому)

```bash
dotnet add package SetNet
```

### Локально

Додайте посилання на проект:

```bash
dotnet add reference ../SetNet/SetNet.csproj
```

## Швидкий старт

### Крок 1: Створіть типи повідомлень

```csharp
// MessageTypes.cs
public enum MessageTypes : ushort
{
    PlayerMove = 1,
    ChatMessage = 2,
    ServerUpdate = 3
}

// Класи повідомлень (мають бути сумісні з MessagePack)
[MessagePackObject]
public class PlayerMoveMessage
{
    [Key(0)]
    public float X { get; set; }
    
    [Key(1)]
    public float Y { get; set; }
}

[MessagePackObject]
public class ChatMessage
{
    [Key(0)]
    public string PlayerName { get; set; }
    
    [Key(1)]
    public string Text { get; set; }
}
```

### Крок 2: Створіть сервер

```csharp
using SetNet.Core;
using SetNet.Config;

public class GameServer : BaseServer
{
    public GameServer(Configuration config) : base(config) { }

    protected override BasePeer OnNewClient(PeerInfo peerInfo)
    {
        var peer = new GamePeer(peerInfo);
        peer.StartReceive();
        return peer;
    }
}

// В Program.cs
var config = new Configuration
{
    Host = "127.0.0.1",
    Port = 5682,
    BufferSize = 4096,
    MaxConnections = 100
};

var server = new GameServer(config);
await server.StartAsync();
```

### Крок 3: Створіть клієнт

```csharp
using SetNet.Core;
using SetNet.Config;

public class GameClient : BaseClient
{
    public GameClient(Configuration config) : base(config) { }

    protected override void OnConnected()
    {
        Console.WriteLine("Підключено до сервера!");
        SendPlayerMoveMessage(10.5f, 20.3f);
    }

    protected override void OnDisconnected()
    {
        Console.WriteLine("Відключено від сервера");
    }

    protected override void OnError(string error)
    {
        Console.WriteLine($"Помилка: {error}");
    }

    public async Task SendPlayerMoveMessage(float x, float y)
    {
        await SendAsync<PlayerMoveMessage>(
            (ushort)MessageTypes.PlayerMove,
            new PlayerMoveMessage { X = x, Y = y }
        );
    }
}

// В Program.cs
var config = new Configuration
{
    Host = "127.0.0.1",
    Port = 5682
};

var client = new GameClient(config);
await client.ConnectAsync();
```

## Транспорт: TCP / UDP / Both

Транспорт обирається через `Configuration.TransportType`. За замовчуванням — `Tcp`, тож наявний код працює без змін.

```csharp
using SetNet.Core.Transport;

var config = new Configuration
{
    Host = "127.0.0.1",
    Port = 5682,
    TransportType = TransportType.Both,   // Tcp | Udp | Both
    UdpReliabilityEnabled = true,         // надійний UDP-канал (sequence/ACK/resend)
    DefaultDelivery = DeliveryMethod.Reliable
};
```

`SendAsync` має необов'язковий параметр `DeliveryMethod` (варіант із двома аргументами використовує `Configuration.DefaultDelivery`):

```csharp
await SendAsync(type, msg);                              // DefaultDelivery
await SendAsync(type, msg, DeliveryMethod.Unreliable);  // явний канал
```

Маршрутизація за `(TransportType, DeliveryMethod)`:

| TransportType | DeliveryMethod | Канал |
|---|---|---|
| Tcp  | будь-який | TCP |
| Udp  | Reliable | Шар надійності UDP (потрібен `UdpReliabilityEnabled`, інакше виняток) |
| Udp  | Unreliable | Сирий UDP-датаграма |
| Both | Reliable | TCP |
| Both | Unreliable | UDP (з відкатом на TCP, поки UDP-канал не приєднано) |

Особливості:
- **UDP — з емуляцією з'єднання**: рукостискання призначає ідентичність peer, heartbeat визначає живість, тож `OnConnected`/`OnDisconnected`/`BasePeer` працюють так само, як у TCP.
- **Режим Both**: спершу TCP, сервер передає клієнту UDP-токен по TCP, далі UDP-рукостискання прив'язується до того ж peer. Якщо UDP недоступний — плавний відкат на TCP.
- **MTU**: датаграми більші за `UdpMaxDatagramPayload` (типово 1200 Б) відхиляються (без фрагментації).

Спробувати локально (in-process сценарії):

```bash
dotnet run --project SetNet.Tests -- tcp    # TCP echo
dotnet run --project SetNet.Tests -- udp    # UDP unreliable + reliable echo
dotnet run --project SetNet.Tests -- loss   # надійний UDP під 30% втрат
dotnet run --project SetNet.Tests -- both    # маршрутизація TCP+UDP під втратами
```

## Production-готовність: безпека, ліміти, метрики

Для розгортання поза довіреною мережею передбачено загартування:

```csharp
using System.Security.Cryptography.X509Certificates;

var config = new Configuration
{
    Host = "0.0.0.0",
    Port = 5682,

    // TLS поверх TCP (UDP не шифрується)
    UseSsl = true,
    ServerCertificate = new X509Certificate2("server.pfx", "password"), // на сервері
    // на клієнті: SslTargetHost / ServerCertificateValidationCallback

    // Ліміти / захист від DoS
    MaxConnectionsLimit = 5000,            // стеля одночасних peer'ів
    MaxUdpPeers = 5000,                    // стеля UDP-peer'ів (захист від handshake-флуду)
    MaxMessageSize = 1024 * 1024,          // макс. розмір TCP-кадру (захист від slow-loris/OOM)
    MaxConnectionsPerIpPerSecond = 20,     // per-IP rate-limit на нові з'єднання/рукостискання
    MaxInFlightMessages = 256              // back-pressure: межа одночасних хендлерів на з'єднання
};
```

- **Автентифікація — на боці застосунку**: перевіряйте креденшіали у вашому `OnNewClient`/хендлерах (бібліотека дає лише транспорт).
- **Метрики**: `config.Metrics` — лічильники (надіслано/отримано, прийнято/відхилено з'єднань, ретрансміти/ACK, дропнуті рукостискання); `config.Metrics.Snapshot()` для експорту. `server.ActiveConnections` — поточна кількість peer'ів.
- **Стійкість**: помилка в `OnNewClient` не валить accept-loop; помилки reconnect/heartbeat логуються; винятки хендлерів виносяться через логер.

Тести (`dotnet test`) покривають це: ліміти з'єднань, rate-limit, навантаження багатьма клієнтами, reconnect-шторм (без витоків), back-pressure, fuzzing зламаних кадрів і TLS-round-trip.

## Доставка, порядок і продуктивність

Кілька прапорців у `Configuration` керують тим, як повідомлення відправляються й обробляються. Усі за замовчуванням зберігають початкову поведінку — вмикайте за потреби.

### Порядок обробки (`SequentialDispatch`)

> ⚠️ **Важливо:** за замовчуванням хендлери запускаються «fire-and-forget», тож **порядок їх виконання не гарантований навіть на TCP** — два повідомлення можуть оброблятися паралельно або у зворотному порядку. Якщо логіка залежить від порядку (наприклад, «create» перед «update»), увімкніть послідовний dispatch або серіалізуйте у власному коді.

```csharp
var config = new Configuration { /* ... */ SequentialDispatch = true };
// receive-loop чекає завершення кожного хендлера перед читанням наступного кадру —
// строгий, неперекривний порядок на одному з'єднанні (ціна — менший паралелізм).
```

### Back-pressure (`MaxInFlightMessages`)

```csharp
var config = new Configuration { /* ... */ MaxInFlightMessages = 256 };
// Обмежує кількість одночасних хендлерів на з'єднання; коли межу досягнуто,
// receive-loop призупиняє читання, обмежуючи пам'ять при повільних хендлерах.
```

### Батчинг надсилання (`SendBatching`) — для game-tick патерну

```csharp
var config = new Configuration { /* ... */ SendBatching = true, SendBatchFlushMs = 15 };
// ... у вашому BaseClient/BasePeer за тік:
await SendAsync(type1, msg1);
await SendAsync(type2, msg2);   // акумулюються в буфер
await FlushAsync();             // один запис у сокет замість кількох (менше syscall'ів)
```

Буфер також авто-flush-иться кожні `SendBatchFlushMs`, і коректно зливається при закритті з'єднання.

### Тайм-аут надсилання (`SendTimeoutMs`)

`SendTimeoutMs` (типово `30000`) обмежує час одного запису в сокет. Якщо peer перестав читати (мертвий-але-не-RST / zero-window), запис не «зависає» назавжди, тримаючи lock — з'єднання розривається. Встановіть `0`, щоб вимкнути.

### Незалежні надійні UDP-канали (`UdpReliableChannels`)

```csharp
var config = new Configuration
{
    TransportType = TransportType.Udp,   // або Both
    UdpReliabilityEnabled = true,
    UdpReliableChannels = 2               // канал 0 і канал 1 — незалежні впорядковані потоки
};

// 4-арг SendAsync обирає канал:
await SendAsync(type, movement, DeliveryMethod.Reliable, channel: 0);
await SendAsync(type, chat,     DeliveryMethod.Reliable, channel: 1);
// Втрата пакета на каналі 1 (chat) не затримує доставку на каналі 0 (movement).
```

## Обробка розривів і перепідключення

`BaseClient` розрізняє навмисний і неочікуваний розрив. Перевизначте потрібні хуки:

```csharp
public class GameClient : BaseClient
{
    public GameClient(Configuration config) : base(config) { }

    protected override void OnConnected()    { /* з'єднано */ }
    protected override void OnDisconnected() { /* з'єднання закрито (рівно один раз на з'єднання) */ }
    protected override void OnError(string error) { /* лише неочікувана помилка */ }

    protected override void OnUnexpectedDisconnect() { /* сервер впав / мережа */ }
    protected override void OnReconnecting(int attempt, int max) => Console.WriteLine($"Reconnect {attempt}/{max}");
    protected override void OnReconnected()    { /* успішно перепідключено */ }
    protected override void OnReconnectFailed(){ /* усі спроби вичерпано */ }
}
```

Увімкнути авто-reconnect:

```csharp
var config = new Configuration
{
    Host = "127.0.0.1", Port = 5682,
    AutoReconnect = true, MaxReconnectAttempts = 5, ReconnectDelayMs = 1000
};
```

| Подія | OnError | OnUnexpectedDisconnect | OnDisconnected | Auto-Reconnect |
|---|---|---|---|---|
| `Disconnect()` (навмисно) | ❌ | ❌ | ✅ | ❌ |
| Помилка мережі / краш сервера | ✅ | ✅ | ✅ (якщо reconnect провалився) | ✅ (якщо увімкнено) |
| Graceful close сервером | ❌ | ❌ | ✅ | ❌ |

На сервері `BasePeer` так само розрізняє навмисний `Close()` (kick) від неочікуваного розриву клієнта через `OnError`/`OnUnexpectedDisconnect`. `OnDisconnected` гарантовано спрацьовує **рівно один раз** на з'єднання.

## Робота з повідомленнями

### Створення обробника повідомлень

#### На сервері

```csharp
using SetNet.Core;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;

[MessageHandler((ushort)MessageTypes.PlayerMove)]
public class PlayerMoveHandler : IServerMessageHandler
{
    public async Task HandleAsync(BasePeer peer, byte[] data)
    {
        // Десеріалізуємо повідомлення
        var message = MessagePackSerializer.Deserialize<PlayerMoveMessage>(data);
        
        Console.WriteLine($"Гравець {peer.CurrentPeerInfo.Id} рухається: ({message.X}, {message.Y})");
        
        // Виконайте обробку
        // Наприклад, оновіть стан гравця на сервері
        
        // Опціонально: відправте відповідь клієнту
        // await peer.SendAsync<SomeResponse>((ushort)MessageTypes.UpdateState, response);
    }
}
```

#### На клієнті

```csharp
[MessageHandler((ushort)MessageTypes.ServerUpdate)]
public class ServerUpdateHandler : IClientMessageHandler
{
    public async Task HandleAsync(byte[] data)
    {
        var message = MessagePackSerializer.Deserialize<ServerUpdateMessage>(data);
        
        Console.WriteLine($"Оновлення від сервера: {message.Timestamp}");
        // Оновіть клієнтський стан
    }
}
```

### Відправка повідомлень

**З клієнта на сервер:**

```csharp
// В класі, який наслідує BaseClient
public async Task SendChatMessage(string name, string text)
{
    await SendAsync<ChatMessage>(
        (ushort)MessageTypes.ChatMessage,
        new ChatMessage 
        { 
            PlayerName = name, 
            Text = text 
        }
    );
}
```

**З сервера на клієнта:**

```csharp
// В обробнику на сервері
await peer.SendAsync<ServerUpdateMessage>(
    (ushort)MessageTypes.ServerUpdate,
    new ServerUpdateMessage 
    { 
        Timestamp = DateTime.UtcNow 
    }
);
```

## Расширені приклади

### Використання GameLoopScheduler

```csharp
using SetNet.Utils;

var scheduler = new GameLoopScheduler();

// Додайте завдання, яке виконується кожні 100ms
scheduler.Every(100, async () =>
{
    Console.WriteLine("Обновлення гейм-лупу...");
    // Виконайте обновлення логіки гри
    await Task.CompletedTask;
});

// Запустіть у фоні
scheduler.StartInBackground();

// Якщо потрібно зупинити
// await scheduler.StopAsync();
```

### Використання EventManager

```csharp
using SetNet.Events;

var eventManager = new EventManager();

// Підпишіться на подію
eventManager.Subscribe("PlayerJoined", data =>
{
    if (data is string playerName)
    {
        Console.WriteLine($"Гравець {playerName} приєднався!");
    }
});

// Викличте подію
eventManager.Trigger("PlayerJoined", "Alex");
```

### Повна приклад сервера з обробниками

```csharp
using SetNet.Core;
using SetNet.Config;
using SetNet.Data;
using SetNet.Data.Attributes;
using SetNet.Messaging;
using MessagePack;

// Обробник для повідомлень про рух
[MessageHandler((ushort)MessageTypes.PlayerMove)]
public class PlayerMoveHandler : IServerMessageHandler
{
    public async Task HandleAsync(BasePeer peer, byte[] data)
    {
        var message = MessagePackSerializer.Deserialize<PlayerMoveMessage>(data);
        Console.WriteLine($"Гравець рухається до ({message.X}, {message.Y})");
        
        // Відправте підтвердження
        await peer.SendAsync<PlayerMoveMessage>(
            (ushort)MessageTypes.PlayerMove,
            message
        );
    }
}

// Обробник для чат-повідомлень
[MessageHandler((ushort)MessageTypes.ChatMessage)]
public class ChatHandler : IServerMessageHandler
{
    public async Task HandleAsync(BasePeer peer, byte[] data)
    {
        var message = MessagePackSerializer.Deserialize<ChatMessage>(data);
        Console.WriteLine($"{message.PlayerName}: {message.Text}");
        await Task.CompletedTask;
    }
}

public class GamePeer : BasePeer
{
    public GamePeer(PeerInfo peerInfo) : base(peerInfo) { }

    protected override void OnDisconnected()
    {
        Console.WriteLine($"Гравець {CurrentPeerInfo.Id} відключився");
    }
}

public class GameServer : BaseServer
{
    public GameServer(Configuration config) : base(config) { }

    protected override BasePeer OnNewClient(PeerInfo peerInfo)
    {
        var peer = new GamePeer(peerInfo);
        peer.StartReceive();
        return peer;
    }
}

// Program.cs
class Program
{
    static async Task Main()
    {
        var config = new Configuration
        {
            Host = "127.0.0.1",
            Port = 5682,
            BufferSize = 4096
        };

        var server = new GameServer(config);
        await server.StartAsync();
    }
}
```

## Архітектура

### Основні компоненти

```
┌─────────────────────────────────────────────────┐
│              Клієнт / Сервер                     │
│          (BaseClient / BaseServer)              │
└────────────────┬────────────────────────────────┘
                 │
        ┌────────┴────────┐
        │                 │
┌───────▼────────┐  ┌────▼──────────┐
│  BaseSocket    │  │ CommandExecutor│
│ (мережа)       │  │ (рефлексія)    │
└───────┬────────┘  └────┬──────────┘
        │                 │
┌───────▼────────────────▼────────┐
│    MessageProcessor              │
│    PacketBuilder                 │
│    MessagePackSerializer          │
│   (серіалізація + маршрутизація)│
└─────────────────────────────────┘
        │
┌───────▼──────────────────────────┐
│   Handler Classes                │
│   (IServerMessageHandler)         │
│   (IClientMessageHandler)         │
└──────────────────────────────────┘
```

### Потік повідомлення

1. **Відправка**: `SendAsync<T>()` → Серіалізація → PacketBuilder → NetworkStream
2. **Отримання**: NetworkStream → PacketBuilder → Десеріалізація → Handler

## Налаштування конфігурації

```csharp
var config = new Configuration
{
    Host = "192.168.1.100",      // IP адреса
    Port = 5682,                  // Порт (TCP; UDP теж використовує його, якщо UdpPort = 0)
    BufferSize = 8192,            // Розмір буфера читання (байти)

    // Транспорт
    TransportType = TransportType.Tcp,        // Tcp | Udp | Both
    DefaultDelivery = DeliveryMethod.Reliable,// для 2-арг SendAsync(type, msg)
    UdpPort = 0,                              // 0 = використати Port

    // Надійність UDP
    UdpReliabilityEnabled = true,
    UdpReliableChannels = 1,                  // незалежні впорядковані канали (щоб втрата на одному не блокувала інший)
    UdpReliableAckTimeoutMs = 100,
    UdpReliableWindowSize = 64,               // 1..64 (ACK — 64-бітне бітове поле)
    UdpMaxDatagramPayload = 1200,             // без фрагментації: більше — відхиляється
    UdpOrderedReliable = true,

    // Емуляція з'єднання UDP
    UdpHandshakeTimeoutMs = 5000,
    UdpPeerExpiryMs = 15000,

    // Heartbeat (типово вимкнено; увімкніть для виявлення «мертвих» з'єднань)
    HeartbeatEnabled = false,
    HeartbeatIntervalMs = 5000,
    HeartbeatTimeoutMs = 15000,

    // Reconnect (клієнт)
    AutoReconnect = false,
    MaxReconnectAttempts = 3,
    ReconnectDelayMs = 1000,
    ConnectTimeoutMs = 10000,

    // Dispatch / надсилання
    MaxInFlightMessages = 0,    // 0 = без back-pressure (хендлери fire-and-forget); >0 = межа одночасних хендлерів
    SequentialDispatch = false, // true = чекати завершення кожного хендлера перед наступним кадром (строгий порядок)
    SendBatching = false,       // true = коалесувати TCP-надсилання в один запис (для game-tick патерну)
    SendBatchFlushMs = 15,      // авто-flush буфера батчингу
    SendTimeoutMs = 30000,      // межа на один запис у сокет; 0 = вимкнено (захист від «застряглого» peer)

    // TLS поверх TCP (UDP не шифрується)
    UseSsl = false,
    // ServerCertificate / SslTargetHost / ServerCertificateValidationCallback

    // Ліміти / захист від DoS
    MaxConnections = 100,                  // базова стеля з'єднань
    MaxConnectionsLimit = 0,               // якщо >0 — переважає MaxConnections
    MaxUdpPeers = 1000,
    MaxMessageSize = 1024 * 1024,
    MaxConnectionsPerIpPerSecond = 0       // 0 = вимкнено
};
```

## Компіляція та запуск

### Побудувати проект

```bash
dotnet build
```

### Запустити тестовий клієнт

```bash
dotnet run --project SetNet.Tests
```

### Режим Release

```bash
dotnet build -c Release
dotnet run --configuration Release --project SetNet.Tests
```

### Запустити тести (xUnit)

```bash
dotnet test SetNet.UnitTests/SetNet.UnitTests.csproj
```

Покриває фреймінг (`PacketBuilder`, у т.ч. фрагментацію), UDP-формат, шар надійності (порядок/дедуп), `MessageProcessor`, `CommandExecutor`, валідацію конфігу, а також інтеграційні round-trip тести для TCP / UDP / UDP-під-втратами / Both.

## Приклад: чат (окремо сервер і клієнт)

У теці `examples/` — повноцінний консольний чат на базі бібліотеки: спільний контракт повідомлень (`Chat.Shared`), окремий сервер (`Chat.Server`) і окремий клієнт (`Chat.Client`). Сервер веде реєстр peer'ів і розсилає (broadcast) повідомлення всім; клієнт надсилає рядки й отримує трансляції та системні сповіщення про вхід/вихід.

Запуск (сервер в одному терміналі, кілька клієнтів — в інших):

```bash
# Термінал 1 — сервер
dotnet run --project examples/Chat.Server -- 127.0.0.1 5000

# Термінал 2 — клієнт "alice"
dotnet run --project examples/Chat.Client -- 127.0.0.1 5000 alice

# Термінал 3 — клієнт "bob"
dotnet run --project examples/Chat.Client -- 127.0.0.1 5000 bob
```

Друкуйте рядки й тисніть Enter, щоб надіслати; `/quit` — вихід. Усі підключені клієнти бачать повідомлення одне одного.

## Поширені помилки та розв'язання

### Обробник не викликається

**Проблема**: Ваш обробник не отримує повідомлення

**Розв'язання**:
- ✓ Переконайтеся, що клас реалізує `IServerMessageHandler` або `IClientMessageHandler`
- ✓ Клас має атрибут `MessageHandler` з правильним типом повідомлення
- ✓ Тип повідомлення (ushort) відповідає тому, що відправляється
- ✓ Клас в assemblies, які завантажуються AppDomain

```csharp
// ✓ Правильно
[MessageHandler((ushort)MessageTypes.PlayerMove)]
public class PlayerMoveHandler : IServerMessageHandler { }

// ✗ Неправильно (пропущено [MessageHandler])
public class PlayerMoveHandler : IServerMessageHandler { }
```

### Проблеми з з'єднанням

**Проблема**: Клієнт не може підключитися до сервера

**Розв'язання**:
- ✓ Сервер запущено
- ✓ Адреса (Host) та Порт однакові на обох сторонах
- ✓ Брандмауер дозволяє з'єднання

```csharp
// На сервері та клієнті має бути одне й те саме
var config = new Configuration
{
    Host = "127.0.0.1",
    Port = 5682
};
```

### Повідомлення видаляються

**Проблема**: Клієнт/сервер отримує неповні або пошкоджені дані

**Розв'язання**:
- ✓ Переконайтеся, що класи повідомлень правильно позначені `[MessagePackObject]`
- ✓ Всі поля мають `[Key(N)]` атрибути
- ✓ BufferSize достатньо великий (за замовчуванням 4096)

## Контрибʼютинг

Ми вітаємо внески! Будь ласка:

1. Fork проект
2. Створіть feature гілку (`git checkout -b feature/AmazingFeature`)
3. Commit змін (`git commit -m 'Add some AmazingFeature'`)
4. Push на гілку (`git push origin feature/AmazingFeature`)
5. Відкрийте Pull Request

## Ліцензія

Цей проект розповсюджується під MIT ліцензією - див. файл [LICENSE](LICENSE) для деталей.

## Автор

Створено Artem Lemeshev  
Email: povstalez@gmail.com

## Подяки

- MessagePack для ефективної серіалізації
- .NET community за вдохновлення та підтримку

---

**Встава запитання або проблеми?** Відкрийте issue на GitHub або зв'яжіться з автором!
