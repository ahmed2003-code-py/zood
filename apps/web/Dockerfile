# ZAWED web image: the built React app served by Caddy, which also proxies /api to the API.

FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
# mock = in-browser demo data; http = the ZAWED API (set as a Railway service variable).
ARG VITE_API_MODE=http
ENV VITE_API_MODE=$VITE_API_MODE
RUN npm run build

FROM caddy:2-alpine
COPY Caddyfile /etc/caddy/Caddyfile
COPY --from=build /app/dist /srv
# Railway sets PORT; API_UPSTREAM is the API's private address, e.g. http://zawed-api.railway.internal:8080
EXPOSE 8080
