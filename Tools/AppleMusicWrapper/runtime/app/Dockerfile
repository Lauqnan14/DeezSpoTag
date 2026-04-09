FROM ubuntu:24.04

WORKDIR /app
#COPY --from=builder /app /app
COPY . /app
ENV args ""

CMD ["bash", "-c", "./wrapper ${args}"]

EXPOSE 10020 20020
