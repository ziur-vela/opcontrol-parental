# Parental OpControl

[English](README.md) · [Español](README.es.md)

Software de control parental autoalojado para familias. Tiene un panel web para
padres, un cliente de Windows para la computadora del niño y un instalador guiado.
Está disponible en inglés y español.

![Panel familiar](assets/screenshots/dashboard.png)

## Funciones

- Bloqueo y desbloqueo instantáneo, horarios, tiempo extra y PIN de anulación.
- Tiempo de pantalla por día y por aplicación, con informes y actividad.
- Filtro de sitios web por categorías y bloqueo de aplicaciones en tiempo real.
- Vista remota de pantalla y cámara, con aviso opcional en el dispositivo.
- Alertas por correo, resumen diario y mensajes entre padres e hijos.
- Varias familias por servidor, invitaciones, permisos por padre y 2FA.
- Cliente de Windows con actualizaciones automáticas y reversión.

## Instalar en tu propio servidor

Necesitas una máquina Linux con Docker Engine y el complemento Docker Compose,
un dominio y HTTPS (un proxy inverso o túnel delante del puerto web).

1. Descarga `docker-compose.yml`, `.env.example` y `LICENSE` de la última
   versión: <https://github.com/ziur-vela/parental-opcontrol/releases>.
2. Colócalos en una carpeta, por ejemplo `/opt/stacks/opcontrol-parental`.
3. Copia `.env.example` a `.env` y completa:
   - `POSTGRES_PASSWORD`: una contraseña larga y aleatoria.
   - `UPDATER_SECRET`: ejecuta `openssl rand -hex 32`.
   - `STACK_DIR`: la ruta absoluta de esta carpeta.
   - `PUBLIC_BASE_URL`: la dirección que usarán las familias, por ejemplo
     `https://parental.example.com`.
4. Inicia: `docker compose up -d`.
5. Lee el token de configuración en el registro:
   `docker compose logs web | grep 'FIRST-RUN SETUP'`.
6. Abre `https://tu-direccion/setup`, ingresa el token, crea el primer
   administrador del servidor, escanea el código QR con una aplicación de
   autenticación y, si quieres, agrega SMTP.
7. Agrega un dispositivo desde **Agregar dispositivo** e instala el cliente de
   Windows.

La página de configuración desaparece después de crear el primer administrador.
Las demás familias se unen por invitación desde Administración del servidor. El
contenedor web no tiene acceso al socket de Docker; solo el contenedor de
actualizaciones lo tiene.

## Actualizar

Administración del servidor → **Actualizaciones** busca versiones estables en
GitHub cada seis horas. Cuando haya una versión nueva, pulsa **Actualizar ahora**
(con un código de dos factores si tu cuenta usa 2FA). El actualizador:

1. respalda la base de datos con `pg_dump` (conserva los últimos cinco respaldos),
2. descarga la imagen firmada por digest,
3. recrea el contenedor web,
4. espera hasta tres minutos a que `/health` informe la versión nueva,
5. vuelve a la imagen anterior si la verificación de salud falla.

Los dispositivos de los niños se actualizan desde su propio servidor con el
flujo de actualización existente. Las versiones preliminares nunca se ofrecen en
el canal estable. Las instalaciones sin conexión pueden desactivar la búsqueda en
GitHub en la misma página.

Las notas de despliegue de nuestro homelab están en
[`docs/HOMELAB-DEPLOY.md`](docs/HOMELAB-DEPLOY.md).

## Licencia

Parental OpControl es gratis para uso personal y otros usos no comerciales bajo
la [PolyForm Noncommercial License 1.0.0](LICENSE). Puedes usarlo, modificarlo y
compartirlo con fines no comerciales, y debes conservar el aviso requerido:

`Required Notice: Copyright (c) 2026 Ziur / OpControl IT Solutions (https://opcontrol.net)`

El uso comercial (venderlo, ofrecerlo como servicio pagado o usarlo dentro de una
empresa) requiere permiso por escrito de OpControl IT Solutions. Contacto:
`hendry.ruiz@opcontrol.net`.

El proyecto podría pasar a una licencia de código abierto (AGPL-3.0) si recibe
contribuciones. Al enviar una contribución aceptas que pueda distribuirse bajo
esta licencia y bajo cualquier licencia futura elegida por el titular de los
derechos, incluida AGPL-3.0. Consulta [CONTRIBUTING.md](CONTRIBUTING.md).

Los componentes de terceros, las fuentes y las fuentes de datos están en
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
