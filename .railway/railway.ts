// Railway Infrastructure as Code for the zawed-dev project (ADR 0016). It replaces the deprecated
// apps/api/railway.json and apps/web/railway.json, which Railway stops reading on 2026-12-01.
//
// Imported with `railway config pull` (the live services, the database and its volume), plus the
// healthchecks that lived in railway.json. Variables stay preserve(): their values live only in
// Railway and never in this repository. Each service is deployed with `railway up` from its own
// folder (apps/api, apps/web), whose Dockerfile Railway builds.
//
// This file is linked to zawed-dev only. The investor demo (project "zood") is never managed here.
import { defineRailway, postgres, preserve, project, service, volume } from "railway/iac";

export default defineRailway(() => {
  const Postgres = postgres("Postgres", { region: "us-west2" });
  Postgres.networking = { privateNetworkEndpoint: "postgres" };
  const postgresVolume = volume("postgres-volume", {
    alerts: { usage: { "100": {}, "80": {}, "95": {} } },
    allowOnlineResize: true,
    region: "us-west2",
    sizeMB: 5000,
  });
  const zawedApi = service("zawed-api", {
    replicas: { "us-west2": 1 },
    // Ready only after Flyway has migrated and the database answers; a slow first boot is normal.
    healthcheck: "/api/v1/health/readiness",
    healthcheckTimeout: 180,
    // Graceful shutdown (ADR 0011): in-flight requests finish before the old instance stops.
    deploy: { drainingSeconds: 30, restartPolicyType: "ON_FAILURE", restartPolicyMaxRetries: 3 },
    env: {
      PGDATABASE: preserve(),
      PGHOST: preserve(),
      PGPASSWORD: preserve(),
      PGPORT: preserve(),
      PGUSER: preserve(),
      PORT: preserve(),
      SPRING_PROFILES_ACTIVE: preserve(),
      ZAWED_CORS_ALLOWED_ORIGINS: preserve(),
      ZAWED_DATA_KEY: preserve(),
      ZAWED_ENVIRONMENT: preserve(),
      ZAWED_JWT_SECRET: preserve(),
    },
  });
  const zawedWeb = service("zawed-web", {
    replicas: { "us-west2": 1 },
    healthcheck: "/",
    deploy: { restartPolicyType: "ON_FAILURE" },
    env: { API_UPSTREAM: preserve(), VITE_API_MODE: preserve() },
  });

  return project("zawed-dev", {
    resources: [Postgres, zawedApi, zawedWeb, postgresVolume],
  });
});
