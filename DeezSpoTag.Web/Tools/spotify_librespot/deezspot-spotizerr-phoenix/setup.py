from setuptools import setup, find_packages

README = open("README.md", "r")
readmed = README.read()
README.close()

setup(
	name = "deezspot-spotizerr-phoenix",
	version = "1.2.0",
	description = "Spotizerr's implementation of deezspot",
	long_description = readmed,
	long_description_content_type = "text/markdown",
	license = "GNU Affero General Public License v3",
	python_requires = ">=3.10",
	author = "spotizerr-phoenix",
	url = "https://lavaforge.org/spotizerrphoenix/deezspot-spotizerr-phoenix",

	packages = find_packages(include=["deezspot", "deezspot.*"]),

        install_requires = [
                "mutagen==1.47.0",
                "pycryptodome==3.23.0",
                "requests==2.32.3",
                "tqdm==4.67.1",
                "fastapi==0.116.1",
                "uvicorn[standard]==0.35.0",
                "librespot-spotizerr-phoenix",
								"rapidfuzz==3.13.0",
								"spotipy==2.25.1"
         ],
)
