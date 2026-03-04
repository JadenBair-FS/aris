FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy all source files
COPY . .

# Restore and publish the API project
RUN dotnet restore "src/ARIS.API/ARIS.API.csproj"
RUN dotnet publish "src/ARIS.API/ARIS.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Generate the runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http+:8080

COPY --from=build /app/publish .

# Run the API
ENTRYPOINT ["dotnet", "ARIS.API.dll"]
