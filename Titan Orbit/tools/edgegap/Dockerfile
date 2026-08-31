# Titan Orbit — custom Edgegap Docker image.
# Point the Edgegap plugin "Path to Dockerfile" field at: tools/edgegap/Dockerfile
# Build path in the plugin should stay: Builds/EdgegapServer (matches TitanOrbit → Build → Headless Server (Linux — Edgegap)).
FROM ubuntu:22.04

ARG DEBIAN_FRONTEND=noninteractive
ARG SERVER_BUILD_PATH=Builds/EdgegapServer

COPY ${SERVER_BUILD_PATH} /root/build/

WORKDIR /root/build/

RUN chmod +x /root/build/ServerBuild* 2>/dev/null || true && \
    chmod +x /root/build/*.x86_64 2>/dev/null || true

RUN apt-get update && \
    apt-get install -y ca-certificates && \
    apt-get clean && \
    update-ca-certificates

COPY tools/edgegap/start-server.sh /root/start-server.sh
RUN chmod +x /root/start-server.sh

# Do not set SDL_VIDEODRIVER=dummy — NullGfx PresentAndWait (~300 ms/frame, ~11 Hz wall).
ENV DISPLAY=

CMD ["/root/start-server.sh"]
