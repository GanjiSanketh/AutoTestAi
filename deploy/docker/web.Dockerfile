# ---- build (Vite) ----
# Build context is the repository root (see docker-compose.yml).
FROM node:22-alpine AS build
WORKDIR /app
COPY apps/web/package.json apps/web/package-lock.json* ./
RUN npm ci --no-audit --no-fund || npm install --no-audit --no-fund
COPY apps/web ./
ARG VITE_API_BASE_URL=http://localhost:5193
ARG VITE_SIGNALR_HUB_URL=http://localhost:5193/hubs/execution
ARG VITE_KEYCLOAK_URL=http://localhost:8080
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
ENV VITE_SIGNALR_HUB_URL=$VITE_SIGNALR_HUB_URL
ENV VITE_KEYCLOAK_URL=$VITE_KEYCLOAK_URL
RUN npm run build

# ---- runtime (nginx SPA) ----
FROM nginx:alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
COPY deploy/docker/web-nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
