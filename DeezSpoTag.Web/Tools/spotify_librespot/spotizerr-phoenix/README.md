# librespot-spotizerr-phoenix

Spotizerr's librespot python integration - Phoenix fork

## About The Project

This project is to further add more features for the Spotizerr project, forked from [librespot-python](https://github.com/kokarare1212/librespot-python) and [librespot-spotizerr](https://github.com/Xoconoch/librespot-spotizerr)

## Getting Started

### Prerequisites

- [Python](https://python.org/)

### Installation

```commandline
pip install librespot-spotizerr-phoenix
```

## Usage

### Use Zeroconf for Login (no premium required)

```python
from librespot.zeroconf import ZeroconfServer
import time
import logging
import pathlib

zs = ZeroconfServer.Builder().create()
logging.warning("Transfer playback from desktop client to librespot-spotizerr via Spotify Connect in order to store session")

while True:
    time.sleep(1)
    if zs._ZeroconfServer__session:
        logging.warning(f"Grabbed {zs._ZeroconfServer__session} for {zs._ZeroconfServer__session.username()}")

        if pathlib.Path("credentials.json").exists():
            logging.warning("Session stored in credentials.json. Now you can Ctrl+C")
            break
```

### Get Music Stream

\*Currently, music streaming is supported, but it may cause unintended behavior.<br>

```python
from librespot.core import Session
from librespot.metadata import TrackId
from librespot.audio.decoders import AudioQuality, VorbisOnlyAudioQuality

session = Session.Builder() \
    .user_pass("Username", "Password") \
    .create()

track_id = TrackId.from_uri("spotify:track:xxxxxxxxxxxxxxxxxxxxxx")
stream = session.content_feeder().load(track_id, VorbisOnlyAudioQuality(AudioQuality.VERY_HIGH), False, None)
# stream.input_stream.stream().read() to get one byte of the music stream.
```

## Debug

To display the debug information, you need to inject the following code at the
top of the code.

```python
import logging


logging.basicConfig(level=logging.DEBUG)
```

## Contributing

Pull requests are welcome.

## License

Licensed under the Apache License, Version 2.0. See
[LICENSE](https://lavaforge.org/spotizerr/librespot-spotizerr/src/branch/main/LICENSE)
for more information.

## Related Projects

- [Librespot](https://github.com/librespot-org/librespot) (Concept)
- [Librespot-Java](https://github.com/librespot-org/librespot-java) (Core)
