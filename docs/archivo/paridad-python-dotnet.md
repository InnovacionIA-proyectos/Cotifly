# Paridad funcional Python vs .NET (HU-003.9, #15)

> **ARCHIVADO — NO VIGENTE.** Este documento se movió a `docs/archivo/` en HU-022.1 (#146, 2026-10-15). HU-003.9 (#15) se cerró sin ejecutarse: el Python se retiró del repositorio (tag `python-final`). Se conserva solo como registro histórico.

Plantilla para el plan y la ejecución de las pruebas de paridad. La define y la ejecuta Kevin (QA). La aprobación explícita de QA queda en el issue #15 antes de que HU-006 considere el backend .NET listo para producción.

- Estado: **Cerrada por decisión del usuario (2026-10-13, HU-022.1): el Python se retiró del repositorio (tag `python-final`), así que ya no hay contra qué comparar.** No se ejecuta; queda como histórico.
- Responsable: Kevin (QA). Soporte: Duban (backend).

> **Nota (HU-013.9, #105).** Documento histórico de la migración. Google Chat y el agente cotizador conversacional se retiraron del backend .NET en HU-013.9: los casos de Google Chat y los de cotizar conversando por `POST /web/chat` ya no aplican (hoy `/web/chat` es el asistente por folio y sin `folio` responde 400 `FOLIO_REQUERIDO`; `/chat/events` responde 404).

## 1. Objetivo y alcance

Comparar el resultado exacto de al menos **10 casos reales** entre el backend Python actual y el backend .NET, cubriendo distintas líneas de negocio: margen (valor y origen), precio, utilidad y PDF generado. Además, probar con Postman los 5 endpoints `/web/*` del contrato congelado (HU-004.1) y confirmar códigos de estado y forma de respuesta.

La paridad automatizada ya cubre cada caso de `tests/test_precio.py`, `tests/test_margen.py`, SAP TM, PDF, correo, agente y Google Chat (tabla en [Equivalencias de pruebas Python y .NET](#equivalencias-de-pruebas-python-y-net), trasladada desde `08-testing.md`). Esta prueba agrega datos reales y la comparación visual del PDF.

## 2. Diferencias deliberadas (no son hallazgos)

Decididas por el equipo y documentadas; si aparecen, se marcan como "esperada" con la referencia:

| Tema | Python | .NET | Referencia |
|---|---|---|---|
| Redondeo en empates exactos de medio peso | Arrastra el error de `float` | Half-to-even sobre el valor exacto (diferencia máxima 1 peso, ~0,04 % de los casos; ej. 55 con markup 0.1 → 60, Python 61) | ADR-010 §6 |
| Folio | `PCN-DDMMAAAA`, versión `-v2` en silencio, o el `referencia` del LLM | Generado por el servidor: `PCN-[{nit}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]`, con las iniciales del comercial y consecutivo diario por iniciales; una colisión se informa (`FOLIO_DUPLICADO`) | ADR-011 |
| Firma del PDF | Datos que mandaba el LLM | Nombre y correo de `UsuariosWeb`; "Cel" solo con teléfono registrado | [02-arquitectura](../../backend-dotnet/docs/02-arquitectura.md#28-pdf-correo-y-auditoría) |
| SAP TM caído | `MARGEN_FUENTE_NO_DISPONIBLE` | Política de la línea con el motivo en `detalle` | Decisión de #13 (HU-009) |
| Línea con tilde ("Distribución") | Fallaba la cotización entera (issue #44) | Se resuelve | #11 |
| Costo | Tarifario de Google Drive | Retirado: el costeo viene de Nexo (HU-005); mientras tanto el comercial escribe el costo | Plan de migración |
| Margen del LLM | El LLM podía mandar margen o cliente | El estado de la conversación manda (ADR-004): el LLM no pone números | ADR-004 |
| Estado de envío en un reenvío | Quedaba `enviada` | `reenviada` | #11 |
| Endpoints `/web/*` salvo `/web/chat` | Responden 501 desde HU-002.2 | Implementados según el contrato | `docs/docs_front/CONTRATO_API_WEB.md` |
| Texto del agente | Redacción libre | Validada contra un esquema; con respaldo controlado | ADR-003, HU-003.10 |

Lista completa por módulo: secciones "Diferencias deliberadas con el Python" de [02-arquitectura](../../backend-dotnet/docs/02-arquitectura.md).

## 3. Entornos

| Campo | Python | .NET |
|---|---|---|
| URL | | |
| Versión (commit o tag) | | |
| Base de datos | | |
| Fuente de margen (`SapTm:Modo` / `SAP_TM_*`) y export usado | | |
| Proveedor y modelo de IA | | |
| Fecha de ejecución | | |

Para que la comparación sea válida, ambos backends deben usar la **misma** fuente de margen (el mismo export de SAP TM o el mismo servicio OData) y la **misma** política de líneas (`MargenesPolitica`).

## 4. Casos

Criterio de cobertura: las 8 líneas de negocio (Nacional, Distribución, Importación, Exportación, Internacional, Almacenamiento, Almacenamiento3PL, ITR), al menos un cliente con margen de SAP TM, un cliente sin registro en SAP (política), un caso con `markup`, un margen negociado y una cotización de varias secciones.

| Id | Línea | Cliente (BP / NIT) | Ruta | Costo | Método | Qué ejercita |
|---|---|---|---|---|---|---|
| PAR-01 | Nacional | | | | margen | Cliente con margen en SAP TM (`sap_tm`) |
| PAR-02 | Distribución | | | | margen | Línea con tilde; cliente sin registro en SAP (`politica_linea`) |
| PAR-03 | Importación | | | | margen | |
| PAR-04 | Exportación | | | | margen | |
| PAR-05 | Internacional | | | | margen | Bloque aduanero en el PDF |
| PAR-06 | Almacenamiento | | | | markup | Método `markup` |
| PAR-07 | Almacenamiento3PL | | | | margen | Nombre de línea con número |
| PAR-08 | ITR | | | | margen | |
| PAR-09 | Nacional | | | | margen | Margen negociado por el comercial (`negociado`) |
| PAR-10 | Varias secciones | | | | margen | Variante multi del PDF (2 páginas) |
| PAR-11 | (opcional) | | | | | Margen anómalo de SAP (fuera de 0 a 60 %) |
| PAR-12 | (opcional) | | | | | Precio con empate de medio peso (diferencia esperada) |

Resultados (una fila por caso):

| Id | Margen Python (valor / origen) | Margen .NET (valor / origen) | Precio Python | Precio .NET | Utilidad Python | Utilidad .NET | PDF (páginas, bloques, textos) | ¿Igual? | Diferencia y clasificación |
|---|---|---|---|---|---|---|---|---|---|
| PAR-01 | | | | | | | | | |
| PAR-02 | | | | | | | | | |
| PAR-03 | | | | | | | | | |
| PAR-04 | | | | | | | | | |
| PAR-05 | | | | | | | | | |
| PAR-06 | | | | | | | | | |
| PAR-07 | | | | | | | | | |
| PAR-08 | | | | | | | | | |
| PAR-09 | | | | | | | | | |
| PAR-10 | | | | | | | | | |

Clasificación de una diferencia: **esperada** (tabla de la sección 2), **defecto .NET**, **defecto Python** (el .NET corrige) o **por decidir** (se prioriza con el equipo).

Comparación del PDF: páginas, orden de los bloques, textos estándar, bloques condicionales (aduana, extradimensionada, refrigerada), imagen y leyenda de la línea, tabla de tarifas, nota de decretos, firma y pie. Referencia de la comparación visual de #11: `tests/artefactos/` (se genera al correr las pruebas).

## 5. Endpoints `/web/*` con Postman

Los "5 endpoints `/web/*`" de HU-003.9 son los que consumía `chat.html` en el Python y que congeló HU-004.1: `POST /web/login`, `POST /web/chat`, `GET /web/mis-cotizaciones`, `POST /web/cotizaciones/{folio}/enviar` y `POST /web/cotizaciones/{folio}/seguimiento`. En el contrato actual `/web/login` ya no existe: lo reemplaza `POST /api/auth/login-clientepro` (login SSO, ADR-009), que se prueba en su lugar (WEB-01). Los otros cuatro conservan ruta y forma (con los renombres de HU-002.2 documentados en `docs/docs_front/CONTRATO_API_WEB.md` §7). El contrato actual tiene además endpoints nuevos, fuera de esta tabla (ver el final de la sección).

Contra el backend .NET, confirmar código de estado y forma de la respuesta contra [`specs/openapi.yaml`](../../backend-dotnet/specs/openapi.yaml):

| Id | Endpoint | Casos mínimos | Esperado | Obtenido | Estado |
|---|---|---|---|---|---|
| WEB-01 | `POST /api/auth/login-clientepro` (reemplaza `/web/login`) | Usuario válido; usuario sin rol | 200 `LoginResponse`; 401 `Credenciales inválidas` | | |
| WEB-02 | `POST /web/chat` | Mensaje válido; `sesion_id` inválido; sin token; sesión de otro comercial | 200 `RespuestaChat`; 400; 401; 404 | | |
| WEB-03 | `GET /web/mis-cotizaciones` | Con token; sin token | 200 `MisCotizaciones`; 401 | | |
| WEB-04 | `POST /web/cotizaciones/{folio}/enviar` | Envío válido; duplicado; folio ajeno; correo deshabilitado | 200 `RespuestaEnvio`; 400 `CORREO_DUPLICADO`; 404; 503 | | |
| WEB-05 | `POST /web/cotizaciones/{folio}/seguimiento` | Ganada; perdida con motivo; motivo > 2000; folio ajeno | 200 `CotizacionResumen`; 200; 400; 404 | | |

Los endpoints nuevos del contrato (teléfono, nueva versión, destinatarios previos, vista previa, administración, métricas, `/pdfs`) se cubren en la [regresión manual](../../backend-dotnet/docs/qa/regresion-manual.md) y en las pruebas automáticas.

## 6. Hallazgos y priorización

| Id | Caso | Descripción | Clasificación | Prioridad acordada | Issue | Estado |
|---|---|---|---|---|---|---|
| | | | | | | |

## 7. Aprobación

| Rol | Nombre | Fecha | Decisión |
|---|---|---|---|
| QA | Kevin | | |
| Backend | Duban | | |

## Equivalencias de pruebas Python y .NET

> **Histórico. HU-003.9 (#15) cerrada por decisión del usuario (2026-10-13, HU-022.1): el Python se retiró del repositorio (tag `python-final`), así que ya no hay contra qué comparar.** Esta sección queda como trazabilidad: los `test_*.py` que cita
> están en el tag `python-final`.

> Las filas del agente conversacional y de Google Chat (`clsAgenteRunnerTests`, `clsInyeccionEstadoTests`, `clsGoogleChat*Tests`, `clsChatEventosEndpointTests`, `chat_slot_filling.feature`, `google_chat.feature`, `chat_web.feature`) son históricas: esas pruebas se borraron con el retiro de HU-013.9 (#105) y el comportamiento del Python que verificaban ya no existe en Cotifly.

- **Paridad con el Python (#10, #13).** Cada caso de `tests/test_precio.py` y `tests/test_margen.py` tiene su prueba .NET (el comentario sobre cada prueba nombra el test de origen):

| Test Python | Prueba .NET |
|---|---|
| `test_valores_exactos_arquitectura` | `clsCalculadoraPrecioTests.ValoresExactosDeLaArquitectura`; `precio.feature` |
| `test_markup_vs_margen` | `clsCalculadoraPrecioTests.MarkupRindeMenosPrecioQueMargen`; `precio.feature` |
| `test_borde_margen_cero` | `clsCalculadoraPrecioTests.ConMargenCeroElPrecioEsElCosto`; `precio.feature` |
| `test_borde_margen_uno_division_por_cero` | `clsCalculadoraPrecioTests.ConMetodoMargenUnMargenDeUnoSeRechaza`; `precio.feature` |
| `test_markup_admite_margen_alto` | `clsCalculadoraPrecioTests.MarkupAdmiteUnMargenAlto`; `precio.feature` |
| `test_rechaza_margen_negativo` | `clsCalculadoraPrecioTests.RechazaMargenNegativo`; `precio.feature` |
| `test_rechaza_costo_no_positivo` | `clsCalculadoraPrecioTests.RechazaCostoNoPositivo`; `precio.feature` |
| `test_rechaza_metodo_desconocido` | `clsCalculadoraPrecioTests.RechazaMetodoDesconocido`; `precio.feature` |
| `test_property_precio_no_menor_que_costo` (hypothesis) | `clsCalculadoraPrecioTests.ElPrecioNuncaEsMenorQueElCostoYLaUtilidadEsLaDiferencia` (grilla determinista de 112 casos, sin FsCheck) |
| `test_normalizar_value` | `clsReglasMargenTests.NormalizaElValueDeSapAFraccionConSeisDecimales` |
| `test_es_anomalo` | `clsReglasMargenTests.MarcaAnomaloFueraDeCeroASesentaPorCiento` |
| `test_resolver_sap_tm` | `clsMargenResolverServiceTests.ConBpYRegistroEnSapElOrigenEsSapTm`; `clsMargenResolverIntegracionTests` (mismo nombre); `margen.feature` |
| `test_resolver_fallback_cliente_nuevo` | `clsMargenResolverServiceTests.SinBpNoSeConsultaSapYSeUsaLaPoliticaDeLaLinea`; `clsMargenResolverIntegracionTests.SinBpSeUsaLaPoliticaSembrada`; `margen.feature` |
| `test_resolver_fallback_cliente_sin_registro` | `ConBpSinRegistroEnSapSeUsaLaPolitica` (unitaria e integración); `margen.feature` |
| `test_resolver_marca_anomalo_negativo` | `UnMargenSapNegativoSeMarcaAnomalo` (unitaria e integración); `margen.feature` |
| `test_resolver_marca_anomalo_atipico` | `UnMargenSapAtipicoSeMarcaAnomalo` (unitaria e integración); `margen.feature` |
| `test_resolver_respeta_sales_org` | `SeRespetaLaOrganizacionDeVentasPedida` (unitaria e integración); `margen.feature` |
| `test_resolver_linea_desconocida` | `UnaLineaDesconocidaDevuelveMargenLineaDesconocida` (unitaria e integración); `margen.feature` |
| `test_mapeo_todas_las_lineas` | `clsMargenResolverIntegracionTests.CadaLineaSembradaTieneSuTrqTypeYSuPolitica` |
| `test_export_*`, `test_odata_*`, `test_construir_resolver_*`, `test_conmutacion_export_a_odata_misma_salida` | **Retiradas en HU-042.2 (#243, ADR-027)** con los modos `export_json`/`odata`: el margen sale de la copia local. Sus equivalentes: `clsFuenteMargenTablaLocalIntegracionTests` (cada cliente con su fila, BP con o sin ceros a la izquierda, fila inactiva, anómalo, sincronización vieja, sin corrida `ok` = no configurado, base sin el 17 = no disponible, mapeo por id con comodín); `clsRegistroFuenteMargenTests` (registro y opciones); `clsAvisosArranqueSapTmTests` (variables retiradas); `clsMargenSapAnomaloEndpointsTests.ConLaCopiaLocalUnMargenSapAnomaloTambienUsaLaPoliticaYQuedaAvisado` (#139 con la fuente real); `margen.feature` ("copia local", incluido el BP con comillas) |
| (sin equivalente en Python: SAP no configurado → política) | `clsMargenResolverServiceTests.ConLaFuenteNoConfiguradaSeUsaLaPoliticaSinError`; `clsMargenSapNoConfiguradoIntegracionTests` (la base compartida nunca tiene una corrida `ok`); `clsFuenteMargenTablaLocalIntegracionTests.SinNingunaCorridaOkLaCopiaSeTrataComoNoConfigurada`; `margen.feature` |
| `registrar_ajuste_margen` (`server.py`, sin test propio) | `clsAjusteMargenServiceTests`; `clsMargenResolverIntegracionTests.ElAjusteNegociadoSePersisteConElComercialYLaCotizacion`; `margen.feature`; auditado: `clsAuditoriaServiceTests.ElAjusteDeMargenNegociadoSeAudita`, `auditoria.feature` |
| `test_html_contiene_datos_de_la_cotizacion` | `clsConstructorDocumentoCotizacionTests.ElDocumentoTieneLosDatosDeLaCotizacion`; `clsGeneradorPdfMigraDocTests.LaSolicitudDeEjemploTieneTresPaginasConLosBloquesEnSuSitio`; `pdf.feature` |
| `test_html_determinista` | `clsConstructorDocumentoCotizacionTests.ElDocumentoEsDeterminista`; `clsGeneradorPdfMigraDocTests.ElRenderEsEstableEnPaginasYTexto` |
| `test_bloque_aduanero_solo_internacional` | `clsConstructorDocumentoCotizacionTests.ElBloqueAduaneroSoloApareceEnLaLineaInternacional`; `clsGeneradorPdfMigraDocTests.ElBloqueAduaneroSoloApareceEnInternacional`; `pdf.feature` |
| `test_bloque_extradimensionada_y_refrigerada` | `clsConstructorDocumentoCotizacionTests.LosBloquesExtradimensionadaYRefrigeradaMuestranSoloLoQueTraen`; `clsGeneradorPdfMigraDocTests.ElBloqueAduaneroSoloApareceEnInternacional` |
| `test_multi_seccion_usa_armar_multi` | `clsConstructorDocumentoCotizacionTests.ConSeccionesSeUsaLaVarianteMulti`; `clsGeneradorPdfMigraDocTests.LaVarianteMultiTieneDosPaginasConLasCondicionesEnLaPrimera`; `pdf.feature` |
| `test_render_pdf_genera_archivo_y_paginas` (weasyprint) | `clsGeneradorPdfMigraDocTests.LaSolicitudDeEjemploTieneTresPaginasConLosBloquesEnSuSitio` (exactamente 3 páginas; el Python pedía ≥ 3) |
| `test_folio_versionado` | Sin equivalente a propósito: el folio lo genera #12 (ADR-011); aquí la colisión es `FOLIO_DUPLICADO` (`clsGenerarPdfServiceTests.UnFolioExistenteEsFolioDuplicadoSinRenderizar`, `pdf.feature`) |
| (sin equivalente en Python: folio con las iniciales del comercial, ADR-011) | Iniciales: `clsInicialesComercialTests` (primer nombre y primer apellido, tildes y Ñ, tres palabras, una palabra, respaldo `nombreUsuario` sin dominio, `XX`, partículas, guion bajo, texto libre de `DESCRIPCIONUSUARIO` entre paréntesis o tras ` - `, inicial abreviada, `LimitacionesConocidasDocumentadasEnElAdr`); formato: `clsFolioTests`; generador: `clsGeneradorFolioServiceTests.LaPrimeraCotizacionDelDiaEsLa001ConLasInicialesYElCandadoDeLasInicialesDelDia`, `ElConsecutivoSigueAlUltimoDeLasInicialesEseDiaYLlevaElNit`, `UnComercialQueNoExisteEnUsuariosWebNoRecibeFolio`; integración: `clsFolioIntegracionTests.DosComercialesConInicialesDistintasEmpiezanAmbosEn001AunqueGenerenALaVez`, `DosComercialesConLasMismasInicialesCompartenElConsecutivoSinColisionar`, `ElConsecutivoEsPorInicialesYCuentaLosFoliosConYSinNit`, `ElUltimoConsecutivoNoCuentaVersionesNiFoliosDeOtrasInicialesODiasNiHeredados`, `DiezGeneracionesSimultaneasDelMismoComercialObtienenConsecutivosUnicos`; `folio.feature` (9 escenarios) |
| (sin equivalente en Python: versión de un folio heredado sin iniciales) | `clsGeneradorFolioServiceTests.LaVersionNuevaDeUnFolioHeredadoSinInicialesLlevaV2SobreSuBase`; `clsFolioIntegracionTests.LaVersionNuevaDeUnFolioHeredadoSinInicialesEsV2SobreSuBase`, `LaVersionMaximaDeUnFolioHeredadoEscapaLosComodinesDeLike` |
| `test_generar_pdf_dod_via_mcp` | `clsGenerarPdfServiceTests.GeneraGuardaYRegistraLaCotizacionConSuTrazabilidad`; `clsPdfCorreoIntegracionTests.GenerarElPdfRegistraLaCotizacionGuardaElArchivoYAudita`; `pdf.feature` |
| `test_generar_pdf_linea_negocio_desconocida_hoy_falla_entero` (pin del bug #44) | Corregido: `clsGenerarPdfServiceTests.UnaLineaDesconocidaSeRechazaAntesDeRenderizar`; con tilde se resuelve: `clsPdfCorreoIntegracionTests`, `pdf.feature` |
| `test_generar_pdf_idempotencia_versiona` | Reemplazado por `FOLIO_DUPLICADO` sin sobrescribir el PDF: `clsGenerarPdfServiceTests.SiElArchivoYaExisteEnElAlmacenNoSeReemplazaYEsFolioDuplicado`, `pdf.feature` |
| `test_generar_pdf_persiste_trazabilidad_completa` | `clsGenerarPdfServiceTests.GeneraGuardaYRegistraLaCotizacionConSuTrazabilidad` (la trazabilidad llega del estado, no del payload) |
| `test_generar_pdf_tolera_cliente_como_objeto` | Sin equivalente: el payload es tipado; el cliente se resuelve por catálogo (HU-005.2) y el NIT llega en la trazabilidad |
| `test_generar_pdf_sin_referencia_error` | `clsGenerarPdfServiceTests.SinFolioNiReferenciaEsPayloadInvalido` |
| `test_generar_pdf_render_no_disponible_error` | `clsGenerarPdfServiceTests.UnFalloDelRenderEsPdfRenderNoDisponible` |
| `test_envio_ok_adjunta_pdf_y_persiste` | `clsCorreoServiceTests.ElEnvioAdjuntaElPdfSaleDelComercialYQuedaRegistrado`; `correo.feature` |
| `test_sin_confirmado_por_rechaza_y_no_envia` | `clsCorreoServiceTests.SinComercialAutenticadoNoSeEnvia` |
| `test_cotizacion_no_encontrada` | `clsCorreoServiceTests.UnaCotizacionAjenaOInexistenteNoSeEnvia`; `correo.feature` |
| `test_pdf_no_encontrado` | `clsCorreoServiceTests.SinPdfEnElAlmacenNoSeEnvia`; `correo.feature` |
| `test_duplicado_menos_5min_rechaza` | `clsCorreoServiceTests.UnEnvioALosMismosDestinatariosEnMenosDeCincoMinutosEsDuplicado`; `clsPdfCorreoIntegracionTests.ElEnvioDeduplicaConElRelojDeLaAplicacionYPasaAReenviada`; `correo.feature` |
| `test_duplicado_mas_5min_permite` | `clsCorreoServiceTests.LaVentanaDeDeduplicacionSeMideConElRelojDeLaAplicacion`; `clsCorreoRepositoryTests.UnEnvioAnteriorAlInstanteNoSeLista`; `correo.feature` |
| `test_distintos_destinatarios_no_es_duplicado` | `clsCorreoServiceTests.OtroConjuntoDeDestinatariosNoEsDuplicado` |
| `test_fallo_smtp_eleva_envio_smtp_error` | `clsCorreoServiceTests.UnFalloDelTransporteQuedaRegistradoComoFallidoYNoCambiaElEstado`; `clsTransporteMailSenderTests.UnMailSenderCaidoEsUnEnvioFallidoSinReintentarElPost` (HU-033); `correo.feature` |
| `test_now_estable_en_transaccion` (Postgres) | Sin equivalente: la ventana se mide con `IClsReloj` (el interceptor fija `createdAt` con el mismo reloj) |
| `test_relay_arma_mensaje_con_remitente_comercial_y_adjunto` | HU-033: `clsTransporteMailSenderTests.UnAltaAceptadaDevuelveElIdDeMailSenderYQuedaEncolada` (el relay ahora lo usa MailSender) |
| `test_relay_sin_confirmado_usa_fallback` | `clsTransporteMailSenderTests.SinCorreoDelComercialElRemitenteEsElPorDefecto` |
| `test_relay_fallo_de_red_eleva_error` | `clsTransporteMailSenderTests.UnMailSenderCaidoEsUnEnvioFallidoSinReintentarElPost` (+ `UnRechazoDeMailSenderEsUnEnvioFallidoConElMotivo`, `SinRespuestaATiempoElEnvioEsInciertoYNoSeReintenta`) |
| `tests/test_ws_correos.py` (WSCorreos SOAP) | Sin equivalente: `wscorreos` se retiró (HU-033, ADR-023: el transporte es MailSender) |
| Middleware de auditoría (`mcp_server/auditoria.py`, cubierto por `test_generar_pdf_dod_via_mcp`) | `clsAuditoriaServiceTests`; `clsPdfCorreoIntegracionTests`; `auditoria.feature`; en el agente, `clsInyeccionEstadoTests.LaAuditoriaDelFiltroNoDuplicaLaDeLosCasosDeUso` |
| `test_loop_tool_use_invoca_mcp_y_cierra` | `clsAgenteRunnerTests.DosHerramientasEncadenadasDevuelvenSuSalidaRealAlModelo` (+ `SinHerramientasElTurnoCierraConLaRespuestaDeLaLlamadaFinal`) |
| `test_tools_declaradas_excluyen_ping` | `clsAgenteRunnerTests.SinHerramientasElTurnoCierraConLaRespuestaDeLaLlamadaFinal` (las 7 declaradas, sin `ping`) y `EnviarCorreoSoloSeDeclaraConElCorreoHabilitado` |
| `test_max_iteraciones_excedidas` | `clsAgenteRunnerTests.SiElModeloNuncaCierraElLoopCortaEnElTope` |
| `test_registra_cada_turno_en_conversacion` | `clsAgenteRunnerTests.CadaTurnoQuedaEnLaConversacionConLasHerramientasYSusSalidas` |
| `test_memoria_entre_turnos_alimenta_contexto` | `clsAgenteRunnerTests.ElSegundoTurnoLlevaLaSolicitudOriginalYLasRespuestasPrevias` |
| `test_integracion_llm_flujo_cotizacion` (marca `llm`, tokens reales) | Sin equivalente automático: las pruebas no consumen tokens (ADR-003); validación manual con el proveedor de producción (pendiente) |
| `test_contexto_antepone_identidad_del_comercial`, `test_ejecutar_turno_envia_system_con_contexto` | `clsAgenteRunnerTests.ElSistemaLlevaElContextoDelComercialYElEstadoResuelto` |
| `test_contexto_vacio_no_modifica_la_skill` | Sin equivalente: el comercial siempre existe (token o email) y el bloque de contexto siempre va |
| `test_invocar_inyecta_space_y_thread_en_tools_que_los_aceptan` | `clsInyeccionEstadoTests.ElClienteIdentificadoEnElTurnoUnoLlegaAlPdfDelTurnoDosSinQueElLlmLoMande` (hilo, comercial, NIT, BP) |
| `test_invocar_no_pisa_valores_del_llm_ni_agrega_no_aceptados` | Invertido a propósito (ADR-004): el estado **sí** pisa lo que manda el LLM: `CalcularPrecioUsaElMargenYElMetodoDelEstadoAunqueElLlmMandeOtros`, `SinClienteConfirmadoObtenerMargenNoConsultaSapConLoQueMandeElLlm` |
| (sin equivalente en Python: el hallazgo del cliente entre turnos) | `clsInyeccionEstadoTests.ElClienteIdentificadoEnElTurnoUnoLlegaAlPdfDelTurnoDosSinQueElLlmLoMande`; `chat_slot_filling.feature` |
| `test_jwt_valido_ack_200`, `test_jwt_ausente_401`, `test_jwt_invalido_401`, `test_bearer_malformado_401` | `clsChatEventosEndpointTests.ConElJwtValidoAcusaDeInmediatoYEncolaElMensaje`, `SinJwtOConUnoInvalidoEs401YNoEncola`; `clsGoogleChatInfraestructuraTests.ElJwtValidoDeLaFixturePasa`, `ElJwtConLaFirmaDeOtraClaveNoPasa`, `SinBearerNoPasa`; `google_chat.feature` |
| `test_ack_no_espera_procesamiento` | `clsChatEventosEndpointTests.ConElJwtValidoAcusaDeInmediatoYEncolaElMensaje` (cola falsa sin worker) |
| `test_evento_no_mensaje_no_encola` | `clsChatEventosEndpointTests.UnEventoQueNoEsMensajeAcusaSinCuerpoYNoEncola`; `clsGoogleChatTests.UnEventoQueNoEsMensajeNoDisparaTurno` |
| `test_parsear_evento_real`, `test_parsear_captura_nombre_del_comercial`, `test_parsear_formato_nuevo_chat_messagepayload` | `clsGoogleChatTests.ParseaElEventoRealDelFormatoClasico`, `CapturaElNombreDelComercial`, `ParseaElFormatoAddOnConElTipoInferido` |
| `test_webhook_sin_jwt_cuando_verificacion_desactivada`, `test_webhook_formato_addon_ack_hostappdataaction` | `clsChatEventosEndpointTests.ElFormatoAddOnAcusaConHostAppDataActionYSinVerificacionNoPideJwt`; `clsGoogleChatInfraestructuraTests.ConLaVerificacionDesactivadaTodoPasa` |
| `test_worker_procesa_encolados`, `test_worker_sobrevive_a_evento_que_falla` | `clsGoogleChatInfraestructuraTests.ElWorkerConsumeLaColaEnSegundoPlano`, `ElWorkerSobreviveAUnEventoQueFallaYAlertaAlSoporte` |
| `test_worker_dispara_runner_y_crea_sesion` | `google_chat.feature` ("El comercial cotiza por Google Chat..."), sesión ligada al comercial |
| `test_google_verifier_pasa_audience_y_certs`, `test_google_verifier_emisor_inesperado`, `test_google_verifier_firma_invalida`, `test_google_verifier_sin_project_number` | `clsGoogleChatInfraestructuraTests.ExigeLaAudienciaDelProyectoElEmisorDeChatYLaVigencia`, `ElJwtConLaFirmaDeOtraClaveNoPasa`, `SinNumeroDeProyectoNadaPasa` |
| `test_formato_cop_y_pct` | `clsGoogleChatTests.FormateaPesosYPorcentajes` |
| `test_mensaje_previsualizacion_desde_calcular_precio`, `test_runner_final_publica_card_previsualizacion` | `clsGoogleChatTests.ConPrecioSePublicaLaPrevisualizacionConLosBotonesDeControlHumano`; `google_chat.feature` |
| `test_mensaje_pdf_desde_generar_pdf` | `clsGoogleChatTests.ConPdfSePublicaLaTarjetaConElEnlaceFirmado` (enlace firmado, ADR-011) |
| `test_mensaje_texto_cuando_no_hay_precio_ni_pdf`, `test_mensaje_ignora_salida_con_error` | `clsGoogleChatTests.SinPrecioNiPdfOConErrorSePublicaTexto` |
| `test_card_pdf_sin_url` | `clsGoogleChatTests.LaTarjetaDelPdfSinUrlNoTieneBoton` |
| `test_publicador_agrega_hilo_y_envia`, `test_construir_publicador_postea_a_chat_api` | `clsGoogleChatInfraestructuraTests.ElPublicadorPosteaALaApiDeChatConElTokenDeLaCuentaDeServicioYElHilo` (+ `LaAsercionDeLaCuentaDeServicioVaFirmadaConSuClave`) |
| `test_publicador_sin_space_falla` | `clsGoogleChatInfraestructuraTests.UnSpaceInvalidoSeRechaza` |
| `test_gating_sin_confirmacion_no_envia_correo` | `clsInyeccionEstadoTests.ElCorreoExigeDestinatariosEscritosPorElComercialYUsaLaCotizacionDelEstado`, `ElPdfSinConfirmacionRespondeConfirmacionRequeridaYNoSeGenera` |
| `test_procesador_no_mensaje_no_publica`, `test_procesador_corre_turno_y_publica` | `clsGoogleChatTests.UnEventoQueNoEsMensajeNoPublica`, `UnComercialConocidoCotizaYLaRespuestaSePublicaEnElHilo` |
| `test_flujo_ajuste_margen_republica` | `clsInyeccionEstadoTests.ElAjusteNegociadoTomaLaReferenciaYElCostoDelEstado` |
| (sin equivalente en Python: identidad de Chat por email, ADR-011) | `clsGoogleChatTests.UnRemitenteSinCuentaRecibeElAvisoYNoCotiza`; `clsAgentePersistenciaTests.ElComercialDeGoogleChatSeEncuentraPorSuCorreoSinDistinguirMayusculas`; `google_chat.feature` |
| `test_recuperar_o_crear_y_aislamiento`, `test_registrar_mensaje_actualiza_actividad` | `clsSesionRepositoryTests` (#9); sesión ligada al comercial: `clsAgenteRunnerTests.LaSesionDeOtroUsuarioNoSeContinua`, `clsChatWebEndpointTests.LaSesionDeOtroComercialEs404` |
| `test_historial_completo_si_cabe`, `test_historial_trunca_conservando_primero`, `test_historial_vacio`, `test_estimar_tokens` | `clsVentanaHistorialTests.SiCabeSeEntregaCompleto`, `TruncaConservandoElPrimeroYLosMasRecientes`, `UnHistorialVacioQuedaVacio`, `EstimaUnTokenCadaCuatroCaracteres` |
| `test_historial_conserva_primero_aunque_exceda` | `clsVentanaHistorialTests.ConservaElPrimeroYElUltimoAunqueExcedan` (el mensaje actual también se conserva) |
| `test_archivar_inactivas_por_timeout` | `clsAgentePersistenciaTests.ElBarridoArchivaLasSesionesInactivasDesdeHaceMasDelTtl` |
| `test_e2e_feliz_cotizar_y_enviar` | `chat_slot_filling.feature` (solicitud → previsualización → confirmación → PDF persistido); el envío, `correo.feature` y `clsInyeccionEstadoTests.ElCorreoExigeDestinatarios...` |
| `test_e2e_sin_tarifa_pide_candidatas_no_inventa` | La fuente de tarifas ya no existe (Drive retirado): `TARIFA_FUENTE_NO_DISPONIBLE` llega al modelo como error estructurado (`clsAgenteRunnerTests.ElErrorEstructuradoDeUnaHerramientaLlegaAlModelo`) y sin margen no hay precio (`SinMargenResueltoCalcularPrecioSeRechazaSinInventarlo`) |
| `test_e2e_margen_anomalo_se_reporta` | `obtener_margen` devuelve `anomalo` tal cual (`clsMargenResolverServiceTests`, #10); el agente lo recibe en `SalidaMargen` |
| `web_chat` (`orquestador/web.py`, sin test propio) | `clsChatWebEndpointTests` (200, 400, 401, 404, `error: true`, 429, respuesta final corregida con un reintento y respaldo de #16); `chat_web.feature`, `validador_respuesta_ia.feature` |
