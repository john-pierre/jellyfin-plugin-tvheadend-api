# TVHeadend Bootstrap — configures TVHeadend for E2E tests after first start.
# Creates test user, removes default admin, sets up IPTV network, EPG, profiles.

FROM python:3.12-alpine

COPY bootstrap.py /bootstrap.py

ENTRYPOINT ["python3", "-u", "/bootstrap.py"]

