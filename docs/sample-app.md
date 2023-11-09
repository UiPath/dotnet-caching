
## Running the sample app

Before running the sample code ensure you have a redis instance 

Go to Sample.AspNetCore folder and start docker-compose. It will start a cluster with 1 master and 2 slaves
```
docker-compose up -d
```
- navigate to http://localhost:8001 to see RedisInsights
- navigate to https://localhost:7020/swagger/index.html after starting the sample
- if you want to simulate another machine (to verify machine syncronization events) start from console
```
dotnet run --launch-profile "Machine2" --no-build
```


