# Playwright execution worker image.
FROM mcr.microsoft.com/playwright:v1.55.0-noble AS base
WORKDIR /app

COPY package.json package-lock.json* ./
RUN npm ci --omit=dev --no-audit --no-fund || npm install --omit=dev --no-audit --no-fund

COPY tsconfig.json ./
COPY src ./src
RUN npm install --no-audit --no-fund && npm run build && npm prune --omit=dev

ENV NODE_ENV=production
ENV WORKER_HEALTH_PORT=8090
EXPOSE 8090
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
  CMD node -e "fetch('http://localhost:8090/health').then(r=>{if(!r.ok)process.exit(1)}).catch(()=>process.exit(1))"
CMD ["node", "dist/index.js"]
