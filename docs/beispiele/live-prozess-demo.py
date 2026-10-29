"""stdout-Beispiel für den grafischen Live-Log-Baustein (ohne Hardware)."""
import time
print("START", flush=True)
for current in range(1, 11):
    print(f"{current} von 10", flush=True)
    time.sleep(0.1)
print("FERTIG", flush=True)
