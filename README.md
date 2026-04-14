# ⚡ Flash Sale API

<p align="center">
  <img src="https://img.shields.io/badge/ASP.NET%20Core-8.0-512BD4?logo=dotnet&logoColor=white" />
  <img src="https://img.shields.io/badge/PostgreSQL-16-4169E1?logo=postgresql&logoColor=white" />
  <img src="https://img.shields.io/badge/Redis-7.x-DC382D?logo=redis&logoColor=white" />
  <img src="https://img.shields.io/badge/Docker-Compose-2496ED?logo=docker&logoColor=white" />
  <img src="https://img.shields.io/badge/Serilog-Enabled-purple" />
  <img src="https://img.shields.io/badge/Swagger-OpenAPI%203.0-85EA2D?logo=swagger&logoColor=black" />
</p>

A **production-grade Flash Sale REST API** built with ASP.NET Core 8, designed to handle **100,000+ concurrent users** without overselling a single product unit.

---

## 🏗️ Architecture Overview

```
┌────────────────────────────────────────────────────────────────┐
│                         API Gateway                            │
│                    (Rate Limiter: 10 req/10s)                  │
└───────────────┬────────────────────────┬───────────────────────┘
                │                        │
   ┌────────────▼──────────┐  ┌──────────▼──────────────┐
   │  FlashSaleController  │  │    OrderController        │
   │  CartController       │  │  POST /api/orders         │
   └────────────┬──────────┘  └──────────┬──────────────┘
                │                        │
   ┌────────────▼──────────┐  ┌──────────▼──────────────┐
   │    Service Layer      │  │   OrderService            │
   │  (Business Logic)     │  │ 1. Idempotency check      │
   └────────────┬──────────┘  │ 2. Lua stock decrement    │
                │             │ 3. Enqueue to Redis        │
   ┌────────────▼──────────┐  │ 4. Return 202 Accepted    │
   │   Repository Layer    │  └──────────┬──────────────┘
   │   (EF Core + LINQ)    │             │
   └────────────┬──────────┘  ┌──────────▼──────────────┐
                │             │  OrderProcessingWorker    │
   ┌────────────▼──────────┐  │  (IHostedService)         │
   │     PostgreSQL 16     │  │  Dequeues → writes DB     │
   │  Orders, Products,    │  │  → clears cart            │
   │  OrderItems           │  └──────────┬──────────────┘
   └───────────────────────┘             │
                                ┌────────▼───────────────┐
                                │         Redis           │
                                │ • Stock counters        │
                                │ • Cart hash (2hr TTL)   │
                                │ • Order queue (FIFO)    │
                                │ • Idempotency keys      │
                                └────────────────────────┘
```

---

## 🛡️ How Overselling is Prevented

This is the most critical design decision. Multiple layers work together:

### Layer 1: Redis Lua Script (Atomic Stock Decrement)

```lua
-- This entire script runs as ONE atomic operation in Redis.
-- No other command can execute between the GET and the DECRBY.
local stock = tonumber(redis.call('GET', KEYS[1]))
if stock == nil then return -1 end       -- product not seeded
if stock < tonumber(ARGV[1]) then return -2 end  -- insufficient
return redis.call('DECRBY', KEYS[1], ARGV[1])   -- decrement!
```

Without this script, thread A and thread B could both read `stock=1`, both pass the check, and both decrement — resulting in `stock=-1` (oversell). The Lua script makes the read-check-decrement an **indivisible unit**.

### Layer 2: Idempotency Keys

The client sends a unique `idempotencyKey` with every order. The API stores it in Redis with `SET NX` (set only if not exists). If the same key arrives again (network retry, accidental double-click), Redis returns 0 and the API returns **409 Conflict**.

### Layer 3: Stock Rollback

If stock decrement succeeds for product A, but product B has insufficient stock, all previously decremented quantities are **restored to Redis** before returning an error.

### Layer 4: Unique DB Constraint

The `IdempotencyKey` column in PostgreSQL has a `UNIQUE` index. Even if two requests slip through Redis, only one will succeed at the database level.

---

## 📦 Project Structure

```
FlashSaleApi/
├── src/
│   └── FlashSaleApi/
│       ├── Controllers/          # HTTP endpoint handlers
│       │   ├── FlashSaleController.cs
│       │   ├── CartController.cs
│       │   └── OrderController.cs
│       ├── Services/             # Business logic layer
│       │   ├── Interfaces/
│       │   ├── FlashSaleService.cs
│       │   ├── CartService.cs
│       │   └── OrderService.cs
│       ├── Repositories/         # Data access layer (EF Core)
│       │   ├── Interfaces/
│       │   ├── FlashSaleRepository.cs
│       │   └── OrderRepository.cs
│       ├── Models/               # EF Core entity models
│       ├── DTOs/                 # Request/Response data contracts
│       │   ├── Requests/
│       │   └── Responses/
│       ├── Infrastructure/
│       │   ├── Data/             # AppDbContext + migrations
│       │   └── Redis/            # Redis service + Lua scripts
│       ├── Workers/              # Background order processor
│       ├── Middleware/           # Global exception handler
│       ├── Program.cs            # DI wiring + startup
│       └── appsettings.json
├── Dockerfile                    # Multi-stage Docker build
├── docker-compose.yml            # API + PostgreSQL + Redis
└── README.md
```

---

## 🚀 Quick Start

### Option A: Docker Compose (Recommended)

```bash
# Clone the repository
git clone https://github.com/yourusername/FlashSaleApi.git
cd FlashSaleApi

# Start all services (API + PostgreSQL + Redis)
docker-compose up --build

# API is now available at:
# http://localhost:5000
# Swagger UI: http://localhost:5000 (in Development mode)
```

### Option B: Local Development

**Prerequisites:** .NET 8 SDK, PostgreSQL, Redis

```bash
# 1. Set up the database
# Update ConnectionStrings in appsettings.json

# 2. Run migrations
cd src/FlashSaleApi
dotnet ef database update

# 3. Start the API
dotnet run

# Swagger UI → https://localhost:5001
```

---

## 📡 API Reference

### Flash Sale Products

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/flashsale/active` | List all active flash sale products |

**Sample Response:**
```json
[
  {
    "id": 1,
    "name": "Sony WH-1000XM5 Headphones",
    "originalPrice": 399.99,
    "discountPrice": 249.99,
    "discountPercentage": 37,
    "stockRemaining": 487,
    "startTime": "2026-04-15T00:00:00Z",
    "endTime": "2026-04-15T05:00:00Z",
    "timeRemaining": "04:32:15"
  }
]
```

---

### Cart

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/cart/{userId}` | Get user's cart |
| POST | `/api/cart` | Add item to cart |
| DELETE | `/api/cart/{userId}/items/{productId}` | Remove single item |
| DELETE | `/api/cart/{userId}` | Clear entire cart |

**Headers required:** `X-User-Id: user123`

**POST /api/cart body:**
```json
{
  "productId": 1,
  "quantity": 2
}
```

---

### Orders

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/orders` | Place a flash sale order |
| GET | `/api/orders/{userId}` | Get order history |

**Headers required:** `X-User-Id: user123`

**POST /api/orders body:**
```json
{
  "idempotencyKey": "550e8400-e29b-41d4-a716-446655440000",
  "items": [
    { "productId": 1, "quantity": 2 },
    { "productId": 3, "quantity": 1 }
  ]
}
```

**Response (202 Accepted):**
```json
{
  "orderId": "7a9b3c1d-2e4f-5a6b-7c8d-9e0f1a2b3c4d",
  "message": "Your order has been received and is being processed.",
  "idempotencyKey": "550e8400-e29b-41d4-a716-446655440000"
}
```

---

## ⚙️ Configuration

| Key | Default | Description |
|-----|---------|-------------|
| `ConnectionStrings:DefaultConnection` | PostgreSQL local | Database connection |
| `ConnectionStrings:Redis` | `localhost:6379` | Redis connection |
| `Serilog:MinimumLevel:Default` | `Information` | Log verbosity |

---

## 🔄 Order Flow (Detailed)

```
Client → POST /api/orders
    │
    ├── [1] Validate request (items, quantities > 0, idempotencyKey present)
    │
    ├── [2] SET NX idempotency key in Redis
    │       └── If already exists → 409 Conflict (duplicate request)
    │
    ├── [3] For each item:
    │       ├── Load product from DB (validate active flash sale window)
    │       └── Run Lua script: atomic Redis stock decrement
    │               ├── Returns -1 → stock key missing → rollback + 409
    │               ├── Returns -2 → out of stock   → rollback + 409
    │               └── Returns N  → success, continue
    │
    ├── [4] Build OrderQueuePayload and LPUSH to Redis "order:queue"
    │
    └── [5] Return 202 Accepted { orderId, message }

Background Worker (OrderProcessingWorker):
    │
    ├── RPOP from "order:queue"
    ├── Write Order + OrderItems to PostgreSQL
    ├── Update order status → Confirmed
    ├── Clear user's cart from Redis (CART:{userId})
    └── On failure: rollback stock in Redis + log for manual review
```

---

## 🔬 Load Testing

Test the anti-oversell guarantee:

```bash
# Install k6
# Create test.js:

import http from 'k6/http';
import { check } from 'k6';

export const options = { vus: 1000, duration: '30s' };

export default function () {
  const res = http.post('http://localhost:5000/api/orders',
    JSON.stringify({
      idempotencyKey: `${__VU}-${__ITER}`,  // unique per virtual user + iteration
      items: [{ productId: 1, quantity: 1 }]
    }),
    {
      headers: {
        'Content-Type': 'application/json',
        'X-User-Id': `user-${__VU}`
      }
    });

  check(res, { 'status is 202 or 409': r => r.status === 202 || r.status === 409 });
}

# Run:
k6 run test.js

# After the test, verify Redis stock matches: (initial - successful 202 orders)
redis-cli GET "stock:1"
```

---

## 🧰 Tech Stack

| Technology | Purpose |
|------------|---------|
| **ASP.NET Core 8** | Web API framework |
| **Entity Framework Core 8** | ORM for PostgreSQL |
| **Npgsql** | PostgreSQL EF Core provider |
| **StackExchange.Redis** | Redis client |
| **Serilog** | Structured logging (Console + File) |
| **Swashbuckle** | Swagger / OpenAPI documentation |
| **ASP.NET Rate Limiter** | Built-in fixed-window rate limiting |
| **Docker + Compose** | Containerization |

---

## 📝 License

MIT License — free to use and modify.
