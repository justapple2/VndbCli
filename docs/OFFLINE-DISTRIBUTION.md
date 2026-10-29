# 跨机器部署与公共资料导入

交付将公共资料、应用和个人灾备分开。公共目录包可以复制到另一机器；完整个人快照及原密钥只在私有运维位置保管。包的平台、版本、源码及文件校验以其封存清单为准。以下说明Linux/amd64 Docker和Windows x64客户端的离线包使用方式；新包名称可随代际变化。

## 包的用途

| 内容 | 用途与边界 |
| --- | --- |
| `vndb-public-data-<日期>.tar.gz` | 官方原始tar.zst、SHA、原文许可／README／时间戳、公开清单及API上传助手。用于建立或补充Web公共库，不含本项目个人资料。 |
| `vndb-offline-deploy-<平台>-<版本>-<代际>.tar.gz` | 已验证Docker镜像、通用Compose、首次初始化／手动备份及源码。镜像可直接load，无需联网构建。 |
| `clients/desktop.tar.gz`、`clients/cli.tar.gz` | Windows x64独立客户端及封存收据；保留整个解压目录。配置／安装／启动仍在用户文档JSON，完整SQLite在Web。 |
| 私有完整snapshot／恢复包 | 个人库灾难恢复：保留原实例、原密钥及匹配镜像。不放入公共分发目录。 |

公共原包不等于整个网站或全部图片。程序仅导入既定公共表白名单，保留官方ODbL／DbCL及来源说明，不执行原包SQL或导入其他用户列表。来源日期和实体计数以包内公开清单及Web完成任务显示为准，不是实时覆盖承诺。

## 新机器操作顺序

先核对交付根的SHA256SUMS及外层包校验文件，再解压应用包和资料包至**不同的全新目录**。每包都有README.zh-CN.md和内部SHA256SUMS。以下路径为示例：

```sh
cd /srv/new-vndb
sha256sum -c SHA256SUMS
docker load -i images/web-linux-x64.tar
sudo sh ./init-new-library.sh
cp -n .env.example .env
# 编辑.env中的本机绑定IP／端口；默认仅127.0.0.1:5187。
docker compose config --quiet
docker compose up -d --no-build --pull never library
curl --fail http://127.0.0.1:5187/healthz
curl --fail http://127.0.0.1:5187/api/v1/info
sh /srv/public-data/import-public-data.sh http://127.0.0.1:5187 ./secrets/library_write_key
```

初始化拒绝已有data／secrets，只创建本项目的新目录和独立密钥；新数据根使用1654:1654。FNOS变更属主后可能改变权限，因此脚本在属主设定之后，仅对这些新目录／文件明确设定权限，不递归修复共享目录。密钥内容不显示。新Web首次运行生成自己的数据库和实例身份，不能复制他人的service-instance-id或管理密钥。

部署模板无build段，数据／密钥绑定设置create_host_path=false；先初始化再启动，避免误创空挂载。ARM64需要另外发布和实机验证。Windows使用该镜像时需要Docker的Linux容器环境；原生桌面端仍通过Web访问完整库。

上传助手需要POSIX sh、curl与sha256sum。它先核验固定原包SHA，再从指定文件读取密钥，通过标准输入传给curl；使用流式POST和固定原包幂等键，不自动重试或新增服务。非标准字符密钥可按[API说明](WEB-API.md)直接调用现有二进制上传接口。

## 完成与容量检查

`POST /api/v1/datasets/upload`接收application/octet-stream，最大512MiB，需要X-Library-Request、X-Library-Key和Idempotency-Key。客户端交换JSON仍走JSON导入；不能把tar.zst当JSON或改名为library.db。

接收到queued只表示排队。进入`/manage.html`手动查看任务，直到completed，再核对公共目录的已验证快照日期和计数。关闭页面不取消已有队列；失败／暂停时先检查，再显式恢复，避免反复上传。全新库的个人条目／评价／游玩记录保持为空，公共资料不会自动加入个人书架。

预检至少保留4GiB额外空间，DB、索引、暂存、图片和快照另计；不要把压缩原包大小当作总容量。宿主数据库在新项目的`data/library.db`，封面为`data/covers`，身份为`data/service-instance-id`，导入输入为`data/datasets`。

从新部署根手动执行`sh ./backup.sh`做完整v2快照，复制到独立介质并另行保管密钥。恢复已有个人库时沿用原身份／密钥和文件属主，使用匹配镜像恢复到新目录；不要再运行首次初始化、覆盖活库或删除在线WAL／SHM。详见[Docker部署](DOCKER.md)和[数据与隐私](DATA-AND-PRIVACY.md)。
