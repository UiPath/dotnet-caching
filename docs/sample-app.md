
## Running the sample app

Before running the sample code ensure you have a redis instance on localhost:6379

```
docker run -d --name redis-stack -p 6379:6379 -p 8001:8001 redis/redis-stack:latest
```