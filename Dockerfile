# One Dockerfile for all eight images.
#
# The alternative is eight near-identical files that drift: one gets a security patch, another does
# not, and the difference is invisible until something breaks in the one nobody looked at. What
# actually differs between these images is a single project path, so that is what the build argument
# carries.
#
# syntax=docker/dockerfile:1

ARG DOTNET_VERSION=9.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG PROJECT
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Restore before the source is copied, so a code change does not invalidate the package cache. The
# solution and props files are what determine the graph, and they change far less often than code.
# .editorconfig is not optional here, and leaving it out cost two failed images.
#
# Warnings are errors in this repo, and .editorconfig is where the generated EF migrations are
# marked generated_code and exempted from style rules. Without it in the build context the analyzers
# apply hand-written-code rules to scaffolded files, and `dotnet publish` fails on CA1861 inside the
# container while the identical build passes on a laptop. A build that only fails in the image is
# the worst kind to debug, because the thing that differs is invisible.
COPY .editorconfig Directory.Build.props Directory.Packages.props global.json Certiflow.sln ./
COPY src/ src/
COPY tests/ tests/

RUN dotnet restore "${PROJECT}"

RUN dotnet publish "${PROJECT}" \
    --configuration ${BUILD_CONFIGURATION} \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# Debian, not Alpine, and that is a decision rather than a default. Directory.Build.props sets
# InvariantGlobalization=false because grounding verification folds diacritics via string.Normalize
# to match French certificate text (NFR-16) - an API that is unsupported in invariant mode. Alpine
# ships without ICU, so it would need icu-libs installed by hand, and forgetting that produces
# "Couldn't find a valid ICU package" at startup: a confusing way to rediscover a decision already
# written down. These images carry ICU already.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
WORKDIR /app

# Non-root. Container Apps does not require it, which is exactly why it is worth doing deliberately
# rather than leaving the default and calling it a platform concern.
RUN useradd --uid 64198 --no-create-home --shell /usr/sbin/nologin certiflow \
 && chown -R certiflow:certiflow /app
USER certiflow

# 8080 rather than 80: a non-root user cannot bind a privileged port, and the ingress in apps.bicep
# targets this.
ENV ASPNETCORE_HTTP_PORTS=8080

EXPOSE 8080

COPY --from=build --chown=certiflow:certiflow /app/publish .

ARG ENTRY_DLL
ENV ENTRY_DLL=${ENTRY_DLL}

# Shell form so ENTRY_DLL expands - one variable is the only thing that differs between these eight
# images. `exec` is not decoration: without it PID 1 is /bin/sh and dotnet is its child, so the
# SIGTERM Container Apps sends on scale-in or a new revision goes to the shell and the app is killed
# rather than shut down. In-flight messages and outbox writes deserve better than that.
ENTRYPOINT exec dotnet ${ENTRY_DLL}
