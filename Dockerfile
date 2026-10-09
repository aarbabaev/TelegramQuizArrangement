FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY TelegramQuizArrangement.csproj ./
RUN dotnet restore
COPY *.cs ./
COPY QuizWording.prompt.txt ./
RUN dotnet publish -c Release --no-restore -o /out /p:UseAppHost=false
RUN dotnet /out/TelegramQuizArrangement.dll --self-test

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out ./
RUN mkdir -p /app/data && chown app:app /app/data
USER app
ENTRYPOINT ["dotnet", "TelegramQuizArrangement.dll"]
