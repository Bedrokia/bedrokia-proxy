# Bedrokia Proxy

Un proxy pour les serveurs [Bedrokia](https://github.com/Nexiumpvp2/bedrokia), comme WaterdogPE. Les joueurs se
connectent au proxy, qui les envoie sur un serveur Bedrokia. Le proxy peut ensuite les faire **changer de serveur
sans reconnexion** : pas d'écran de chargement du serveur ni de retéléchargement des packs.

```
joueur ──NetherNet──> proxy (19132, Xbox Live vérifié ici)
                        ├──> lobby  (Bedrokia, xbox-auth=off)
                        └──> survie (Bedrokia, xbox-auth=off)
```

## Changer de serveur

Rien à changer dans les plugins : un paquet **Transfer** envoyé par un serveur vers un autre serveur de la liste est
intercepté par le proxy, qui fait le changement lui-même. Le menu boussole du Lobby et la commande `/serveur` d'Antafac
marchent donc tels quels.

Le serveur visé par un Transfer est trouvé :

- par son **nom** : `Transfer("survie", 0)` ;
- par **adresse et port** (ceux de `servers`) ;
- par le **port seul** quand un seul serveur l'utilise. Les plugins connaissent souvent l'adresse publique, et le
  proxy l'adresse interne.

Un Transfer vers une adresse inconnue est envoyé au client tel quel : il quitte vraiment le proxy.

Console du proxy : `list`, `send <joueur> <serveur>`, `stop`.

Si un serveur s'arrête ou plante, ses joueurs sont renvoyés sur le serveur `default` au lieu d'être déconnectés.

## Configuration (`proxy.properties`)

Xbox Live est **toujours vérifié** par le proxy, ce n'est pas une option. Ça bloque les comptes crack, le vol de
pseudo et le contournement de ban en changeant de nom.

| Clé | Défaut | Rôle |
|---|---|---|
| `server-port` | `19132` | Port où les joueurs se connectent (TCP + UDP) |
| `public-ip` | vide | IP annoncée aux joueurs (vide : trouvée par STUN dans Docker/Pterodactyl) |
| `webrtc-ports` | `auto` | `auto` = tout le monde sur le port UDP de `server-port`, ou une plage `25600-25630` |

## Les serveurs (`servers.properties`)

Créé au premier démarrage. Un serveur par ligne. Les changements sont pris en compte **dès que le fichier est
enregistré**, sans redémarrer le proxy :

```properties
# Serveur rejoint à la connexion
default=lobby

lobby=172.18.0.1:19135
faction=172.18.0.1:19136
```

Si le fichier contient une erreur, le proxy l'indique dans la console et garde l'ancienne liste. Sur Pterodactyl,
`127.0.0.1` désigne le conteneur du proxy : mets l'IP du nœud, ou `172.18.0.1`.

## Les packs de ressources (`resource_packs/`)

Mets les packs (`.zip`, `.mcpack` ou dossiers) dans `resource_packs/` **du proxy**. Les joueurs les téléchargent une
fois en rejoignant le proxy, et ils servent sur tous les serveurs : les packs des serveurs ne sont plus envoyés.
`force-resources=on` dans `proxy.properties` oblige à les accepter.

Objets et blocs personnalisés : le jeu ne reçoit leur liste qu'une fois par connexion. Entre deux serveurs qui ont
la même liste, le changement est instantané. Vers un serveur qui en a une autre (par exemple du lobby vers un faction
avec ses plugins), le proxy fait reconnecter le jeu directement sur ce serveur : quelques secondes de chargement, et
ses objets et blocs s'affichent. Il lui faut son adresse publique : `public-ip`, ou la détection automatique sous
Docker et Pterodactyl.

## Les serveurs Bedrokia derrière le proxy

1. **`xbox-auth=off`** dans leur `server.properties`. Le proxy vérifie déjà Xbox Live, puis transmet au serveur la
   connexion du joueur (pseudo, XUID, skin). Un serveur `xbox-auth=on` refuse le proxy.
2. **Bloque leurs ports aux joueurs.** Un serveur `xbox-auth=off` accepte n'importe quel pseudo. Si un joueur s'y
   connecte directement, sans passer par le proxy, il peut se faire passer pour un admin. Seul le proxy doit pouvoir
   les joindre : même machine en `127.0.0.1`, réseau privé, ou pare-feu.
3. **Mêmes packs de ressources et même contenu personnalisé** (blocs et items custom) sur tous les serveurs. Le client
   ne reçoit les packs, les items et les blocs qu'une seule fois, de son **premier** serveur.

## Pterodactyl

1. Importe `deploy/pterodactyl/egg-bedrokia-proxy.json` (il est aussi joint à chaque release).
2. Le dépôt est **privé** : remplis la variable **GitHub token** avec un token qui peut lire le dépôt. Un token
   fine-grained limité à `Nexiumpvp2/bedrokia-proxy` avec *Contents: Read-only* suffit.
3. Après le premier démarrage, remplis `servers.properties` dans le gestionnaire de fichiers (voir plus haut).
4. Donne au proxy le port public (19132) ; les serveurs Bedrokia gardent leurs propres ports.

## Développement

```bash
dotnet test BedrokiaProxy.slnx
dotnet run --project src/Bedrokia.Proxy
```

Chaque push sur `main` publie une release (Linux, Windows, egg). Les couches `Bedrokia.Protocol` et
`Bedrokia.Transport.NetherNet` sont copiées de Bedrokia ; le client NetherNet (`NetherNetClient.cs`) est propre au
proxy.

## Comment se fait le changement

1. Le proxy se connecte au nouveau serveur comme un client. Il rejoue le paquet Login du joueur et répond lui-même
   aux packs de ressources.
2. Au `StartGame` du nouveau serveur, il ferme l'ancien serveur, qui sauvegarde le joueur comme pour une
   déconnexion. Il retire ensuite côté client les entités, la liste des joueurs, les effets, les boss bars, les
   scoreboards et le titre de l'ancien serveur.
3. Le client passe par une autre dimension, puis revient dans celle du nouveau monde : il oublie ainsi les anciens
   chunks. Le proxy envoie ensuite le mode de jeu, la difficulté et la position, puis les paquets du nouveau serveur
   mis en attente.
4. L'ID d'entité du joueur change d'un serveur à l'autre. Le proxy le réécrit dans les paquets concernés
   (`EntityIds.cs`).

## Licence

Apache 2.0, voir `LICENSE` et `THIRD-PARTY-NOTICES.md`.
