# syntax=docker/dockerfile:1
# React SPA: build with Node, serve with nginx (which also proxies /api/ to the API container).

# ---- Stage 1: build the static assets ----
FROM node:22-alpine AS build
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# "/" makes the client call relative URLs (API_BASE_URL becomes ""), so every request goes through nginx's /api/ proxy.
ARG VITE_API_BASE_URL=/
ARG VITE_DEMO_MODE=false
ENV VITE_API_BASE_URL=${VITE_API_BASE_URL} VITE_DEMO_MODE=${VITE_DEMO_MODE}
RUN npm run build

# ---- Stage 2: nginx runtime ----
FROM nginx:alpine
COPY --from=build /src/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
