# ⚡ Flash Sale API

A **production-grade, high-throughput Flash Sale REST API** built with **ASP.NET Core 8**, **Redis**, and **PostgreSQL**. Designed to handle **100000+ concurrent users** without overselling a single unit.

---

## 🏗️ Architecture Overview

```
Client (100k req/s)
        │
        ▼
┌─────────────────────────┐
│   ASP.NET Core 8 API    │  ← Rate Limiter, Idempotency Guard, Per-User Quota
└──────────┬──────────────┘
           │
           ▼
┌─────────────────────────┐
│   Redis (Hot Path)       │  ← Atomic Lua batch decrement, Cart, Status, Quotas
└──────────┬──────────────┘
           │  (queue)
           ▼
┌─────────────────────────┐
│  OrderProcessingWorker  │  ← BackgroundService drains queue → DB
└──────────┬──────────────┘
           │
           ▼
┌─────────────────────────┐
│   PostgreSQL 16          │  ← Authoritative record, Unique constraint on IdempotencyKey
└─────────────────────────┘
```

---

## 🔒 Concurrency & Anti-Oversell: 5 Layers of Protection

| Layer | Mechanism | Fixes |
|-------|-----------|-------|
| **1. Batch Lua Script** | Single Redis script checks AND decrements ALL items atomically — zero ghost stock | Ghost Stock Bug |
| **2. Idempotency Key** | Redis SET NX prevents duplicate order submissions | Duplicate orders |
| **3. Per-User Quota** | Redis counter limits units per user per product (configurable) | Bot/scalper abuse |
| **4. DB Unique Constraint** | `IdempotencyKey` is `UNIQUE` in PostgreSQL | Worker retry safety |
| **5. Order Status Polling** | Redis status key tracks Queued→Processing→Confirmed/Failed for client feedback | False Promise Bug |

---

## 📡 API Reference

### Flash Sale Products

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/flashsale/active` | List active products with live stock, discount %, and per-user purchase limit |

### Cart

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/cart` | Add item to cart (validates active sale; stores sale EndTime for auto-eviction) |
| `GET` | `/api/cart/{userId}` | View cart (auto-strips expired sale items) |
| `DELETE` | `/api/cart/{userId}/items/{productId}` | Remove one item |
| `DELETE` | `/api/cart/{userId}` | Clear entire cart |

### Orders

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/orders` | Place order (202 Accepted, async processing) |
| `GET` | `/api/orders/status/{orderId}` | **NEW** — Poll order status (Queued/Processing/Confirmed/Failed) |
| `GET` | `/api/orders/{userId}` | Full order history from PostgreSQL |

---

## 🔄 Order Status Lifecycle

```
POST /api/orders
       │
       ▼ (Redis stock decremented atomically)
   [Queued]  ◄── poll GET /api/orders/status/{orderId}
       │
       ▼ (worker picks up)
 [Processing]
       │
       ├── DB write OK  ──► [Confirmed] ✅
       │
       └── DB write fails ─► [Failed] ❌ (stock compensated back)
```

> **Status keys expire after 24 hours.** After that, use `GET /api/orders/{userId}` for the permanent DB record.

---

## 🛡️ Request Headers

| Header | Required | Description |
|--------|----------|-------------|
| `X-User-Id` | Yes (for cart & orders) | User identifier |
| `Content-Type` | Yes (POST) | `application/json` |

---

## 📦 Place Order Request

```json
POST /api/orders
X-User-Id: user42

{
  "idempotencyKey": "550e8400-e29b-41d4-a716-446655440000",
  "items": [
    { "productId": 1, "quantity": 2 },
    { "productId": 3, "quantity": 1 }
  ]
}
```

**Response — 202 Accepted:**
```json
{
  "orderId": "a1b2c3d4-...",
  "message": "Your order has been received and is being processed. Poll the status URL to confirm.",
  "idempotencyKey": "550e8400-...",
  "statusPollUrl": "/api/orders/status/a1b2c3d4-..."
}
```

---

## 🚀 Running the Project

### Docker (Recommended — zero config)

```bash
git clone https://github.com/soadmahmud/Flash-Sale-API.git
cd Flash-Sale-API
docker-compose up --build
# Swagger UI: http://localhost:5000
```

### Local (requires PostgreSQL + Redis)

```bash
export PATH="$PATH:/home/soadm/.dotnet:/home/soadm/.dotnet/tools"
cd src/FlashSaleApi

# First run only: apply migrations
DOTNET_ROOT=/home/soadm/.dotnet dotnet-ef database update

# Start
dotnet run
# Swagger: http://localhost:5000
```

---

## 🗂️ Project Structure

```
/
├── src/FlashSaleApi/
│   ├── Controllers/         FlashSaleController, CartController, OrderController
│   ├── Services/            FlashSaleService, CartService, OrderService + interfaces
│   ├── Repositories/        FlashSaleRepository, OrderRepository + interfaces
│   ├── Models/              FlashSaleProduct (+ MaxQuantityPerUser), Order, OrderItem
│   ├── DTOs/                Requests & Responses (incl. OrderStatusResponse)
│   ├── Infrastructure/
│   │   ├── Data/            AppDbContext (EF Core + PostgreSQL)
│   │   └── Redis/           RedisService, LuaScripts (DecrementStockBatch)
│   ├── Workers/             OrderProcessingWorker (BackgroundService)
│   ├── Middleware/          ExceptionHandlingMiddleware
│   ├── Migrations/          EF Core migrations
│   └── Program.cs
├── Dockerfile               Multi-stage build (SDK → aspnet:8.0)
├── docker-compose.yml       API + PostgreSQL 16 + Redis 7
└── README.md
```

---

## ⚡ Performance Design

| Hot Path Step | Where | Latency |
|---|---|---|
| Idempotency check | Redis SET NX | ~1ms |
| Per-user quota check | Redis GET | ~1ms |
| Per-user quota update | Redis INCR | ~1ms |
| Batch stock decrement | Redis Lua script (single round-trip) | ~1ms |
| Order status init | Redis HSET | ~1ms |
| Enqueue | Redis LPUSH | ~1ms |
| **Total HTTP response** | — | **~5–10ms** |

DB write happens asynchronously in the background worker.

---

## 🧪 Sample cURL Commands

```bash
# 1. Get active flash sale products (with per-user limits)
curl http://localhost:5000/api/flashsale/active

# 2. Add to cart
curl -X POST http://localhost:5000/api/cart \
  -H "Content-Type: application/json" \
  -H "X-User-Id: user42" \
  -d '{"productId": 1, "quantity": 2}'

# 3. Place order (multi-item)
curl -X POST http://localhost:5000/api/orders \
  -H "Content-Type: application/json" \
  -H "X-User-Id: user42" \
  -d '{
    "idempotencyKey": "my-unique-key-001",
    "items": [
      {"productId": 1, "quantity": 1},
      {"productId": 3, "quantity": 2}
    ]
  }'

# 4. Poll order status (use orderId from step 3 response)
curl http://localhost:5000/api/orders/status/{orderId}

# 5. Order history
curl http://localhost:5000/api/orders/user42
```

---

## 🐳 docker-compose Services

| Service | Image | Port |
|---------|-------|------|
| `api` | Built from `Dockerfile` | `5000` |
| `db` | `postgres:16-alpine` | `5432` |
| `redis` | `redis:7-alpine` | `6379` |

---

## 📋 Rate Limiting

- **Policy:** Fixed Window — 10 requests per 10 seconds per IP address
- **Applied to:** `POST /api/orders` only
- **Response on exceed:** `429 Too Many Requests`

> Per-user purchase quotas (bot protection) are **separate** from rate limiting and are enforced at the business logic level.
