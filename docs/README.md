# Climbing Bot Documentation

클라이밍 봇 프로젝트의 기획 및 모듈별 설계 문서 모음.

## 문서

-   `00_overview.md` --- 전반적인 프로젝트 기획서
-   `01_cv_hold_segmentation.md` --- 홀드 instance segmentation
-   `02_route_extraction.md` --- 루트 클러스터링 및 start/top 추정
-   `03_ar_wall_reconstruction.md` --- 일반 iPhone 기반 wall plane 및
    좌표 복원
-   `04_vlm_motion_planner.md` --- Candidate Generator + VLM 고수준 계획
-   `05_rl_low_level_controller.md` --- ML-Agents ragdoll 저수준 제어
-   `06_ar_visualization.md` --- AR 애니메이션 오버레이
-   `07_interfaces_and_evaluation.md` --- 공통 인터페이스, 통합, 평가

## 시스템 한 줄 정의

**실제 클라이밍 벽을 인식하고, VLM으로 고수준 등반 포즈를 계획하며,
강화학습으로 물리적 움직임을 생성한 뒤 이를 AR로 실제 벽 위에 시각화하는
시스템.**
