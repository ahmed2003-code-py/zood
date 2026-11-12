# ZAWED API image. Built by Railway from this folder (railway.json → DOCKERFILE).
# Tests are not run here: they need the embedded PostgreSQL and run in CI (.github/workflows/ci.yml).

FROM eclipse-temurin:25-jdk AS build
# The Maven wrapper downloads Maven with curl.
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /build
# Dependencies first, so a source-only change reuses this layer.
COPY .mvn .mvn
COPY mvnw pom.xml ./
RUN sh ./mvnw -B -ntp -q dependency:go-offline -Dmaven.test.skip=true || true
COPY src src
RUN sh ./mvnw -B -ntp -Dmaven.test.skip=true package && cp target/zawed-api-*.jar /build/app.jar

FROM eclipse-temurin:25-jre
WORKDIR /app
RUN groupadd --system zawed && useradd --system --gid zawed --home /app zawed
COPY --from=build /build/app.jar /app/app.jar
USER zawed
# Size the heap from the container's memory limit, not the host's.
ENV JAVA_TOOL_OPTIONS="-XX:MaxRAMPercentage=75 -XX:+ExitOnOutOfMemoryError"
EXPOSE 8080
ENTRYPOINT ["java", "-jar", "/app/app.jar"]
